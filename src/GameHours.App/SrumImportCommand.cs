using System.Security.Principal;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Srum;

internal static class SrumImportCommand
{
    public static async Task RunAsync(
        string[] args,
        GameHoursDatabase database,
        CancellationToken cancellationToken = default)
    {
        if (args.Length < 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Usage: GameHours.App srum-import <filter>");
            Console.Error.WriteLine(
                "The development importer currently requires an explicit filter so historical evidence is never bulk-imported by accident.");
            Environment.ExitCode = 2;
            return;
        }

        var source = Environment.GetEnvironmentVariable("GAMEHOURS_SRUM_PATH");
        if (string.IsNullOrWhiteSpace(source))
        {
            source = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "System32",
                "sru",
                "SRUDB.dat");
        }

        var filter = string.Join(" ", args.Skip(1)).Trim();
        var trackingState = new SqliteTrackingStateRepository(database);
        var cutover = await trackingState.GetTrackingStartedAtAsync(cancellationToken);
        if (cutover is null)
        {
            Console.Error.WriteLine("SRUM import requires an existing tracking_started_at cutover.");
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

        Console.WriteLine("GameHours SRUM baseline importer");
        Console.WriteLine($"Source:  {source}");
        Console.WriteLine($"User:    {currentSid}");
        Console.WriteLine($"Cutover: {cutover.Value:O}");
        Console.WriteLine($"Filter:  {filter}");
        Console.WriteLine("Mode:    WRITE historical_evidence; measured sessions and cutover are not modified.");

        try
        {
            var reader = new SrumApplicationUsageReader();
            var rows = reader.Read(source, cutover, currentSid);
            var matchedRows = rows
                .Where(row => row.Application.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            var discovery = new InstalledGameDiscoveryService(
                new IInstalledGameSource[]
                {
                    new SteamInstalledGameSource(),
                    new EpicInstalledGameSource(),
                    new GogInstalledGameSource()
                });
            var installedGames = await discovery.DiscoverAsync(cancellationToken);
            var games = new SqliteGameRepository(database);
            var mappings = new SqliteExecutableMappingRepository(database);
            var resolver = new WindowsGameResolver(installedGames);
            var normalizer = new SrumGameUsageNormalizer(mappings, games, resolver);
            var normalized = await normalizer.NormalizeAsync(matchedRows, cancellationToken);

            Console.WriteLine($"Matched raw rows: {matchedRows.Length}");
            Console.WriteLine($"Normalized games: {normalized.Games.Count}");

            if (normalized.Games.Count == 0)
            {
                Console.WriteLine("Nothing was imported because no conservative game match survived normalization.");
                return;
            }

            var sessions = new SqliteSessionRepository(database);
            var evidenceRepository = new SqliteHistoricalEvidenceRepository(
                database,
                trackingState,
                sessions);
            var importer = new SrumBaselineImporter(games, evidenceRepository);
            var result = await importer.ImportAsync(
                normalized.Games,
                cutover.Value,
                cancellationToken);

            foreach (var item in result.Items)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"{(item.Added ? "ADDED" : "EXISTS"),-6} {item.Game.Title}  " +
                    $"{item.Evidence.Duration.TotalHours:F3} h");
                Console.WriteLine($"       evidence={item.Evidence.Id:D}");
                Console.WriteLine(
                    $"       coverage={item.Evidence.PeriodStartUtc:O} -> {item.Evidence.PeriodEndUtc:O}");
                Console.WriteLine("       source=SRUM kind=Baseline metric=Foreground confidence=Estimated");
            }

            Console.WriteLine();
            Console.WriteLine(
                $"Import complete: added={result.AddedCount}, already-present={result.ExistingCount}.");
            Console.WriteLine("Rerunning the same import is idempotent because the evidence id is deterministic for the game/cutover.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"SRUM import failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }
}
