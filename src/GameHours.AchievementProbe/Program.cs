using System.Text;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Achievements;
using GameHours.Windows.Discovery;

Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

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
var appIdResolver = new SteamCompatibleAppIdResolver();
var sourceLocator = new LocalAchievementSourceLocator();
ILocalAchievementProvider achievementProvider = new AggregatingLocalAchievementProvider();

Console.WriteLine("GameHours local achievement source probe");
Console.WriteLine($"Database: {database.DatabasePath}");
Console.WriteLine($"Filter:   {filter}");
Console.WriteLine("Mode:     read-only; local files only; no Hydra/Steam web API calls.");

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

    var resolvedAppId = result.SteamAppId ?? appIdResolver.TryResolve(executable);
    Console.WriteLine($"  probe root: {result.GameRoot ?? "<unknown>"}");
    Console.WriteLine($"  Steam AppID hint: {resolvedAppId ?? "<none>"}");
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

    IReadOnlyList<LocalAchievementSourceCandidate> compatibilitySources;
    try
    {
        compatibilitySources = sourceLocator.Locate(executable, resolvedAppId);
    }
    catch (Exception exception) when (
        exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
    {
        compatibilitySources = Array.Empty<LocalAchievementSourceCandidate>();
        Console.WriteLine($"  compatibility locator failed: {exception.Message}");
    }

    Console.WriteLine();
    Console.WriteLine($"  compatibility sources: {compatibilitySources.Count}");
    foreach (var source in compatibilitySources)
    {
        Console.WriteLine(
            $"    [{source.Kind}] {source.FilePath}" +
            $"  scope={source.Scope}" +
            (source.AppId is null ? string.Empty : $" appid={source.AppId}"));
    }

    if (compatibilitySources.Count == 0)
    {
        Console.WriteLine("    No supported local compatibility source was found.");
    }

    var achievementRead = achievementProvider.TryReadDetailed(executable);
    var achievementSnapshot = achievementRead.Snapshot;
    Console.WriteLine();
    Console.WriteLine($"  read status:    {achievementRead.Status}");
    Console.WriteLine($"  source health:  {achievementRead.Health}");
    Console.WriteLine($"  state coverage: {achievementRead.StateCoverage}");
    foreach (var diagnostic in achievementRead.Diagnostics)
    {
        Console.WriteLine(
            $"    diagnostic [{diagnostic.Status}] {diagnostic.Provider}: {diagnostic.Detail}" +
            (string.IsNullOrWhiteSpace(diagnostic.SourcePath)
                ? string.Empty
                : $"  path={diagnostic.SourcePath}"));
    }

    if (achievementSnapshot is null)
    {
        Console.WriteLine("  parsed achievements: no trustworthy local snapshot was produced");
        continue;
    }

    var partialCatalogue = !achievementSnapshot.IsCatalogueComplete;
    Console.WriteLine($"  parsed source: {achievementSnapshot.Source}");
    Console.WriteLine($"  catalogue:     {achievementSnapshot.DefinitionPath}");
    Console.WriteLine($"  user state:    {achievementSnapshot.StatePath ?? "<not found; definitions only>"}");
    Console.WriteLine(partialCatalogue
        ? $"  achievements:  {achievementSnapshot.UnlockedCount} unlocked (partial local catalogue; total unknown)"
        : $"  achievements:  {achievementSnapshot.UnlockedCount}/{achievementSnapshot.Achievements.Count} unlocked");

    foreach (var achievement in achievementSnapshot.Achievements.Where(item => item.IsUnlocked))
    {
        Console.WriteLine(
            $"    UNLOCKED {achievement.DisplayName} @ " +
            $"{(achievement.UnlockedAtUtc is null ? "<time unknown>" : achievement.UnlockedAtUtc.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"))}");
    }

    if (partialCatalogue)
    {
        continue;
    }

    var lockedPreview = achievementSnapshot.Achievements
        .Where(item => !item.IsUnlocked)
        .Take(5)
        .ToArray();
    foreach (var achievement in lockedPreview)
    {
        Console.WriteLine($"    LOCKED   {(achievement.Hidden ? "<hidden>" : achievement.DisplayName)}");
    }

    var remainingLocked = achievementSnapshot.Achievements.Count(item => !item.IsUnlocked) - lockedPreview.Length;
    if (remainingLocked > 0)
    {
        Console.WriteLine($"    ... {remainingLocked} more locked achievements");
    }
}
