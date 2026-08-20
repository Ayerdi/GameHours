using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
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
var games = new SqliteGameRepository(database);
var mappings = new SqliteExecutableMappingRepository(database);
var baseResolver = new WindowsGameResolver(installedGames);
var resolver = new LearningGameResolver(baseResolver, mappings, games);

var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "scan";
Console.WriteLine("GameHours development host");
Console.WriteLine($"Database: {database.DatabasePath}");
Console.WriteLine($"Installed games detected: {installedGames.Count}");

if (command is "map")
{
    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: GameHours.App map \"C:\\path\\game.exe\" \"Game title\"");
        Environment.ExitCode = 2;
        return;
    }

    var executablePath = args[1];
    var title = string.Join(" ", args.Skip(2));
    var registration = new ManualGameRegistrationService(games, mappings);

    try
    {
        var game = await registration.RegisterAsync(executablePath, title);
        Console.WriteLine($"Mapped: {Path.GetFullPath(executablePath)}");
        Console.WriteLine($"Game:   {game.Title}");
        Console.WriteLine("Future launches of this exact executable will be tracked automatically.");
    }
    catch (Exception exception) when (
        exception is ArgumentException or FileNotFoundException or NotSupportedException or PathTooLongException)
    {
        Console.Error.WriteLine($"Could not create mapping: {exception.Message}");
        Environment.ExitCode = 2;
    }

    return;
}

if (command is "scan")
{
    foreach (var game in installedGames)
    {
        Console.WriteLine($"  [{game.Source}] {game.Title} -> {game.InstallDirectory}");
    }

    var installedIds = installedGames.Select(game => game.GameId).ToHashSet();
    var rememberedLocalGames = (await games.GetAllAsync())
        .Where(game => !installedIds.Contains(game.Id))
        .GroupBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
        .Select(group => group.First())
        .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    Console.WriteLine($"Remembered local games: {rememberedLocalGames.Length}");
    foreach (var game in rememberedLocalGames)
    {
        Console.WriteLine($"  [Local] {game.Title}");
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
    Console.WriteLine("Run with 'track' to start tracking, 'diagnose' to inspect new processes,");
    Console.WriteLine("or 'map <exe> <title>' to confirm an unknown executable as a game.");
    return;
}

if (command is "diagnose")
{
    using var diagnosticCancellation = CreateConsoleCancellation();
    var diagnosticMonitor = new HybridWindowsProcessMonitor(snapshotProvider, TimeSpan.FromSeconds(1));
    Console.WriteLine("Diagnostic mode. Start an application/game; press Ctrl+C to stop.");
    Console.WriteLine("Only newly started processes are shown. No playtime is recorded and the cutover is unchanged.");

    try
    {
        await foreach (var observation in diagnosticMonitor.ObserveAsync(diagnosticCancellation.Token))
        {
            if (!IsNewProcessStart(observation.Type))
            {
                continue;
            }

            var resolution = await resolver.ResolveAsync(
                new ProcessSnapshot(
                    observation.ProcessId,
                    observation.ProcessName,
                    observation.ExecutablePath,
                    null),
                diagnosticCancellation.Token);

            var label = resolution.Game is null
                ? "UNKNOWN"
                : resolution.IsHelper
                    ? "HELPER"
                    : "GAME";
            var gameTitle = resolution.Game?.Title ?? "-";

            Console.WriteLine(
                $"{label,-7} pid={observation.ProcessId} name={observation.ProcessName} " +
                $"game={gameTitle} method={resolution.Method} confidence={resolution.Confidence:P0}");
            Console.WriteLine($"        path={observation.ExecutablePath ?? "<unavailable>"}");
        }
    }
    catch (OperationCanceledException) when (diagnosticCancellation.IsCancellationRequested)
    {
    }

    return;
}

if (command is not "track")
{
    Console.Error.WriteLine("Usage: GameHours.App [scan|track|diagnose|map <exe> <title>]");
    Environment.ExitCode = 2;
    return;
}

using var cancellation = CreateConsoleCancellation();
var trackingState = new SqliteTrackingStateRepository(database);
var sessions = new SqliteSessionRepository(database);
var openSessions = new SqliteOpenSessionRepository(database);
var monitor = new HybridWindowsProcessMonitor(snapshotProvider, TimeSpan.FromSeconds(1));
var engine = new GameSessionEngine(
    monitor,
    resolver,
    games,
    sessions,
    openSessions,
    trackingState);
engine.Notice += notice =>
{
    switch (notice.Type)
    {
        case TrackingNoticeType.SessionStarted:
            Console.WriteLine($"START     {notice.Game.Title} @ {notice.AtUtc:O} [{notice.Detail}]");
            break;
        case TrackingNoticeType.SessionRecovered:
            Console.WriteLine($"RECOVERED {notice.Game.Title} @ {notice.AtUtc:O} duration={notice.Duration} [{notice.Detail}]");
            break;
        default:
            Console.WriteLine($"STOP      {notice.Game.Title} @ {notice.AtUtc:O} duration={notice.Duration} [{notice.Detail}]");
            break;
    }
};

var cutover = await trackingState.GetTrackingStartedAtAsync();
Console.WriteLine($"Tracking cutover before start: {(cutover is null ? "<not started>" : cutover.Value.ToString("O"))}");
Console.WriteLine("Tracking. Start/close a detected game; press Ctrl+C to stop GameHours.");
Console.WriteLine("Active sessions are checkpointed every 5 seconds for crash/restart recovery.");

try
{
    await engine.RunAsync(cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
}

static CancellationTokenSource CreateConsoleCancellation()
{
    var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, eventArgs) =>
    {
        eventArgs.Cancel = true;
        cancellation.Cancel();
    };
    return cancellation;
}

static bool IsNewProcessStart(ProcessObservationType type) =>
    type is ProcessObservationType.Started or ProcessObservationType.ReconciledStart;
