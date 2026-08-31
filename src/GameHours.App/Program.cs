using System.Security.Principal;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Core.Tracking;
using GameHours.Storage.Sqlite;
using GameHours.Update;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;
using GameHours.Windows.Srum;
using Velopack;

internal static class Program
{
    public static int Main(string[] args)
    {
        // Velopack lifecycle hooks must run directly from the process entry point before
        // normal application initialization. Pending updates are not auto-applied here;
        // GameHours decides explicitly when it is safe to replace the running tracker.
        VelopackApp.Build()
            .SetAutoApplyOnStartup(false)
            .Run();

        RunAsync(args).GetAwaiter().GetResult();
        return Environment.ExitCode;
    }

    private static async Task RunAsync(string[] args)
    {
        var command = args.FirstOrDefault()?.Trim().ToLowerInvariant() ?? "scan";
        if (command is "update-check" or "update-now")
        {
            await HandleUpdateCommandAsync(command, args);
            return;
        }

        if (command is "srum-inspect")
        {
            HandleSrumInspectCommand(args);
            return;
        }

        var dataDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours");
        var databasePath = Path.Combine(dataDirectory, "gamehours.db");

        var database = new GameHoursDatabase(databasePath);
        await database.InitializeAsync();

        if (command is "srum-preview")
        {
            await HandleSrumPreviewCommandAsync(args, database);
            return;
        }

        if (command is "srum-normalize")
        {
            await SrumNormalizedPreviewCommand.RunAsync(args, database);
            return;
        }

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
                    running[resolution.Game.Id] =
                        $"{resolution.Game.Title} ({resolution.Method}, {resolution.Confidence:P0})";
                }
            }

            Console.WriteLine($"Running game candidates: {running.Count}");
            foreach (var candidate in running.Values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  {candidate}");
            }

            Console.WriteLine();
            Console.WriteLine("Run with 'track' to start tracking, 'diagnose' to inspect new processes,");
            Console.WriteLine("'map <exe> <title>' to confirm an unknown executable as a game,");
            Console.WriteLine("'srum-inspect [path]' to inspect an SRUM database schema,");
            Console.WriteLine("'srum-preview [filter]' to preview raw pre-cutover SRUM foreground time,");
            Console.WriteLine("'srum-normalize [filter]' to preview conservative game-normalized SRUM time,");
            Console.WriteLine("or 'update-check <source>' to test the installed-app updater.");
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
            Console.Error.WriteLine(
                "Usage: GameHours.App [scan|track|diagnose|map <exe> <title>|" +
                "srum-inspect [path]|srum-preview [filter]|srum-normalize [filter]|" +
                "update-check <source>|update-now <source>]");
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
    }

    private static void HandleSrumInspectCommand(string[] args)
    {
        var defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "sru",
            "SRUDB.dat");
        var source = args.Length >= 2 ? args[1] : defaultPath;

        Console.WriteLine("GameHours SRUM schema inspector");
        Console.WriteLine($"Source: {source}");
        Console.WriteLine("Read-only diagnostic mode. GameHours tracking state is not opened or modified.");

        try
        {
            var inspector = new SrumDatabaseInspector();
            var tables = inspector.Inspect(source);
            Console.WriteLine($"Tables: {tables.Count}");
            foreach (var table in tables)
            {
                Console.WriteLine($"TABLE {table.Name}");
                Console.WriteLine($"  {string.Join(", ", table.Columns)}");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidOperationException)
        {
            Console.Error.WriteLine($"SRUM inspection failed: {exception.Message}");
            Console.Error.WriteLine(
                "The live Windows SRUDB.dat is normally locked by the SRUM service. " +
                "If this is a sharing/dirty-shutdown error, the next step is a disposable snapshot acquisition path.");
            Environment.ExitCode = 1;
        }
    }

    private static async Task HandleSrumPreviewCommandAsync(
        string[] args,
        GameHoursDatabase database)
    {
        var source = Environment.GetEnvironmentVariable("GAMEHOURS_SRUM_PATH");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "sru",
                "SRUDB.dat");
        }

        var filter = args.Length >= 2
            ? string.Join(" ", args.Skip(1)).Trim()
            : null;
        var trackingState = new SqliteTrackingStateRepository(database);
        var cutover = await trackingState.GetTrackingStartedAtAsync();
        if (cutover is null)
        {
            Console.Error.WriteLine("SRUM preview requires an existing tracking_started_at cutover.");
            Environment.ExitCode = 2;
            return;
        }

        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid))
        {
            Console.Error.WriteLine("Could not determine the current Windows user SID.");
            Environment.ExitCode = 2;
            return;
        }

        Console.WriteLine("GameHours SRUM historical preview");
        Console.WriteLine($"Source:  {source}");
        Console.WriteLine($"User:    {currentSid}");
        Console.WriteLine($"Cutover: {cutover.Value:O}");
        Console.WriteLine("Policy:  current user only; rows after cutover excluded; no historical evidence is persisted.");
        if (!string.IsNullOrWhiteSpace(filter))
        {
            Console.WriteLine($"Filter:  {filter}");
        }

        try
        {
            var reader = new SrumApplicationUsageReader();
            var rows = reader.Read(source, cutover, currentSid);
            var matchedRows = string.IsNullOrWhiteSpace(filter)
                ? rows
                : rows.Where(row => row.Application.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToArray();

            var aggregates = matchedRows
                .GroupBy(row => row.Application, StringComparer.OrdinalIgnoreCase)
                .Select(group => new
                {
                    Application = group.Key,
                    Rows = group.Count(),
                    First = group.Min(row => row.RecordedAtUtc),
                    Last = group.Max(row => row.RecordedAtUtc),
                    FaceTimeTicks = group.Sum(row => row.FaceTime.Ticks)
                })
                .OrderByDescending(item => item.FaceTimeTicks)
                .ThenBy(item => item.Application, StringComparer.OrdinalIgnoreCase)
                .Take(string.IsNullOrWhiteSpace(filter) ? 30 : 100)
                .ToArray();

            Console.WriteLine($"Rows before cutover for current user: {rows.Count}");
            Console.WriteLine($"Matched rows: {matchedRows.Count}");
            Console.WriteLine($"Applications shown: {aggregates.Length}");

            foreach (var aggregate in aggregates)
            {
                var faceTime = TimeSpan.FromTicks(aggregate.FaceTimeTicks);
                Console.WriteLine();
                Console.WriteLine($"{faceTime.TotalHours,10:F3} h  rows={aggregate.Rows,4}  {aggregate.Application}");
                Console.WriteLine($"             first={aggregate.First:O}  last={aggregate.Last:O}");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"SRUM preview failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static async Task HandleUpdateCommandAsync(string command, string[] args)
    {
        var source = args.Length >= 2
            ? args[1]
            : Environment.GetEnvironmentVariable("GAMEHOURS_UPDATE_SOURCE");

        Console.WriteLine("GameHours updater development host");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("No update source was supplied.");
            Console.Error.WriteLine("Pass a release directory/URL or set GAMEHOURS_UPDATE_SOURCE.");
            Environment.ExitCode = 2;
            return;
        }

        try
        {
            var updater = new VelopackUpdateService(source);
            Console.WriteLine($"Source:    {source}");
            Console.WriteLine($"Installed: {updater.IsInstalled}");
            Console.WriteLine($"Version:   {updater.CurrentVersion ?? "<unpackaged>"}");
            Console.WriteLine($"Channel:   {updater.Channel}");

            if (!updater.IsInstalled)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("Update operations require a Velopack-installed copy of GameHours.");
                Console.Error.WriteLine("'dotnet run' builds are intentionally not self-updatable.");
                Environment.ExitCode = 2;
                return;
            }

            var update = await updater.CheckAsync();
            if (update is null)
            {
                Console.WriteLine("GameHours is up to date for this channel.");
                return;
            }

            Console.WriteLine($"Available: {update.Version}");
            Console.WriteLine($"Full size: {update.FullPackageSizeBytes / (1024d * 1024d):F1} MiB");
            Console.WriteLine($"Deltas:    {update.DeltaCount}");
            if (!string.IsNullOrWhiteSpace(update.ReleaseNotesMarkdown))
            {
                Console.WriteLine();
                Console.WriteLine("Release notes:");
                Console.WriteLine(update.ReleaseNotesMarkdown);
            }

            if (command is "update-check")
            {
                return;
            }

            var lastReported = -10;
            var progress = new Progress<int>(value =>
            {
                if (value == 100 || value >= lastReported + 10)
                {
                    lastReported = value;
                    Console.WriteLine($"Download: {value}%");
                }
            });

            await updater.DownloadAsync(update, progress);
            Console.WriteLine("Update downloaded. Preparing graceful exit and restart...");

            // In the future desktop shell this call happens only after active tracking state
            // has been flushed. The updater waits for this process to exit instead of killing it.
            updater.PrepareApplyAndRestart(update, new[] { "scan" });
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Update failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }

    private static CancellationTokenSource CreateConsoleCancellation()
    {
        var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        return cancellation;
    }

    private static bool IsNewProcessStart(ProcessObservationType type) =>
        type is ProcessObservationType.Started or ProcessObservationType.ReconciledStart;
}
