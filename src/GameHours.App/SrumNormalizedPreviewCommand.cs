using System.Security.Principal;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Srum;

internal static class SrumNormalizedPreviewCommand
{
    public static async Task RunAsync(
        string[] args,
        GameHoursDatabase database,
        CancellationToken cancellationToken = default)
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
        var cutover = await trackingState.GetTrackingStartedAtAsync(cancellationToken);
        if (cutover is null)
        {
            Console.Error.WriteLine("Normalized SRUM preview requires an existing tracking_started_at cutover.");
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

        Console.WriteLine("GameHours normalized SRUM game preview");
        Console.WriteLine($"Source:  {source}");
        Console.WriteLine($"User:    {currentSid}");
        Console.WriteLine($"Cutover: {cutover.Value:O}");
        Console.WriteLine("Policy:  read-only; helpers excluded; one max FaceTime row per game/timestamp bucket.");
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
            var result = await normalizer.NormalizeAsync(matchedRows, cancellationToken);

            Console.WriteLine($"Rows before cutover for current user: {rows.Count}");
            Console.WriteLine($"Matched raw rows: {matchedRows.Count}");
            Console.WriteLine($"Normalized games: {result.Games.Count}");

            foreach (var game in result.Games)
            {
                Console.WriteLine();
                Console.WriteLine(
                    $"{game.FaceTime.TotalHours,10:F3} h  {game.Game.Title}  " +
                    $"sourceRows={game.SourceRows} selectedRows={game.SelectedRows}");
                Console.WriteLine(
                    $"             first={game.FirstRecordedAtUtc:O}  last={game.LastRecordedAtUtc:O}");
                foreach (var application in game.Applications)
                {
                    Console.WriteLine($"             include {application}");
                }
            }

            var excluded = result.Decisions
                .Where(decision => !decision.Decision.StartsWith("accepted_", StringComparison.Ordinal))
                .GroupBy(decision => new
                {
                    decision.Application,
                    decision.ResolvedPath,
                    decision.Decision
                })
                .Select(group => new
                {
                    group.Key.Application,
                    group.Key.ResolvedPath,
                    group.Key.Decision,
                    Rows = group.Count(),
                    FaceTimeTicks = group.Aggregate(
                        0L,
                        (current, decision) => checked(current + decision.FaceTime.Ticks))
                })
                .OrderByDescending(item => item.FaceTimeTicks)
                .ThenBy(item => item.Application, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (excluded.Length > 0)
            {
                Console.WriteLine();
                Console.WriteLine("Excluded/unresolved application evidence:");
                foreach (var item in excluded)
                {
                    var faceTime = TimeSpan.FromTicks(item.FaceTimeTicks);
                    Console.WriteLine(
                        $"{faceTime.TotalHours,10:F3} h  rows={item.Rows,4}  [{item.Decision}] " +
                        $"{item.ResolvedPath ?? item.Application}");
                }
            }

            Console.WriteLine();
            Console.WriteLine("No HistoricalEvidence was persisted.");
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidOperationException or OverflowException)
        {
            Console.Error.WriteLine($"Normalized SRUM preview failed: {exception.Message}");
            Environment.ExitCode = 1;
        }
    }
}
