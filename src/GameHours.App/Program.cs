using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Tracking;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;

var dataDirectory = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "GameHours");
var databasePath = Path.Combine(dataDirectory, "gamehours.db");

var database = new GameHoursDatabase(databasePath);
await database.InitializeAsync();

var snapshotProvider = new WindowsProcessSnapshotProvider();
var discovery = new InstalledGameDiscoveryService(
    new IInstalledGameSource[]
    {
        new SteamInstalledGameSource(),
        new EpicInstalledGameSource(),
        new GogInstalledGameSource()
    });
var installedGames = await discovery.DiscoverAsync();
var resolver = new WindowsGameResolver(installedGames);

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "scan";
Console.WriteLine("GameHours development host");
Console.WriteLine($"Database: {database.DatabasePath}");
Console.WriteLine($"Installed games detected: {installedGames.Count}");

if (command is "scan")
{
    foreach (var game in installedGames)
    {
        Console.WriteLine($"  [{game.Source}] {game.Title} -> {game.InstallDirectory}");
    }

    var snapshot = await snapshotProvider.GetSnapshotAsync();
    var running = new Dictionary<Guid, string>();
    foreach (var process in snapshot)
    {
        var resolution = await resolver.ResolveAsync(process);
        if (resolution.Game is not null && !resolution.IsHelper && resolution.Confidence >= 0.80)
        {
            running[resolution.Game.Id] = $"{resolution.Game.Title} ({resolution.Method}, {resolution.Confidence:P0})";
        }
    }

    Console.WriteLine($"Running game candidates: {running.Count}");
    foreach (var candidate in running.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
    {
        Console.WriteLine($"  {candidate}");
    }

    Console.WriteLine();
    Console.WriteLine("Run with 'track' to start persistent local playtime tracking.");
    return;
}

if (command is not "track")
{
    Console.Error.WriteLine("Usage: GameHours.App [scan|track]");
    Environment.ExitCode = 2;
    return;
}

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellation.Cancel();
};

var trackingState = new SqliteTrackingStateRepository(database);
var games = new SqliteGameRepository(database);
var sessions = new SqliteSessionRepository(database);
var monitor = new HybridWindowsProcessMonitor(snapshotProvider, TimeSpan.FromSeconds(1));
var engine = new GameSessionEngine(monitor, resolver, games, sessions, trackingState);
engine.Notice += notice =>
{
    if (notice.Type is TrackingNoticeType.SessionStarted)
    {
        Console.WriteLine($"START {notice.Game.Title} @ {notice.AtUtc:O} [{notice.Detail}]");
    }
    else
    {
        Console.WriteLine($"STOP  {notice.Game.Title} @ {notice.AtUtc:O} duration={notice.Duration} [{notice.Detail}]");
    }
};

var cutover = await trackingState.GetTrackingStartedAtAsync();
Console.WriteLine($"Tracking cutover before start: {(cutover is null ? "<not started>" : cutover.Value.ToString("O"))}");
Console.WriteLine("Tracking. Start/close a detected game; press Ctrl+C to stop GameHours.");

try
{
    await engine.RunAsync(cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}
