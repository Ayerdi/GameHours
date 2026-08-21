using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Achievements;
using GameHours.Windows.Discovery;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: GameHours.AchievementProbe <game filter>");
    Environment.ExitCode = 2;
    return;
}

var filter = string.Join(" ", args).Trim();
var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GameHours");
var databasePath = Path.Combine(dataDirectory, "gamehours.db");

var database = new GameHoursDatabase(databasePath);
await database.InitializeAsync();

var games = new SqliteGameRepository(database);
var mappings = new SqliteExecutableMappingRepository(database);
var knownGames = (await games.GetAllAsync())
    .Where(game => game.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))
    .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
    .ToArray();

if (knownGames.Length == 0)
{
    Console.Error.WriteLine($"No remembered game matched '{filter}'.");
    Environment.ExitCode = 2;
    return;
}

var discovery = new InstalledGameDiscoveryService(
    new IInstalledGameSource[]
    {
        new SteamInstalledGameSource(),
        new EpicInstalledGameSource(),
        new GogInstalledGameSource()
    });
var installedGames = await discovery.DiscoverAsync();
var installedById = installedGames.ToDictionary(game => game.GameId);
var probe = new LocalAchievementProbe();

Console.WriteLine("GameHours local achievement source probe");
Console.WriteLine($"Database: {database.DatabasePath}");
Console.WriteLine($"Filter:   {filter}");
Console.WriteLine("Mode:     read-only; no achievement state is written or changed.");

foreach (var game in knownGames)
{
    var gameMappings = await mappings.GetForGameAsync(game.Id, includeHelpers: false);
    var executable = gameMappings
        .Select(mapping => mapping.ExecutablePath)
        .FirstOrDefault(File.Exists)
        ?? gameMappings.Select(mapping => mapping.ExecutablePath).FirstOrDefault();

    Console.WriteLine();
    Console.WriteLine($"GAME {game.Title}");
    Console.WriteLine($"  id: {game.Id:D}");

    if (string.IsNullOrWhiteSpace(executable))
    {
        Console.WriteLine("  executable: <none remembered>");
        Console.WriteLine("  result: cannot probe game-local sources without a remembered executable.");
        continue;
    }

    var knownInstallDirectory = installedById.TryGetValue(game.Id, out var installed)
        ? installed.InstallDirectory
        : null;

    Console.WriteLine($"  executable: {executable}");
    if (!string.IsNullOrWhiteSpace(knownInstallDirectory))
    {
        Console.WriteLine($"  discovered install: {knownInstallDirectory}");
    }

    LocalAchievementProbeResult result;
    try
    {
        result = probe.Probe(game.Title, executable, knownInstallDirectory);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
    {
        Console.WriteLine($"  probe failed: {exception.Message}");
        continue;
    }

    Console.WriteLine($"  probe root: {result.GameRoot ?? "<unknown>"}");
    Console.WriteLine($"  Steam AppID hint: {result.SteamAppId ?? "<none>"}");
    Console.WriteLine($"  findings: {result.Findings.Count}");

    foreach (var finding in result.Findings)
    {
        Console.WriteLine($"    [{finding.Kind}] {finding.Path}");
        Console.WriteLine($"        {finding.Detail}");
    }

    if (result.Findings.Count == 0)
    {
        Console.WriteLine("    No obvious local achievement source was found by the conservative probe.");
    }
}
