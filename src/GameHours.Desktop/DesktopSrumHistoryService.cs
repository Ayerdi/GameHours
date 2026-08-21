using System.Security.Principal;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Srum;

namespace GameHours.Desktop;

internal sealed record DesktopSrumHistoryCandidate(
    SrumNormalizedGameUsage Usage,
    bool AlreadyImported)
{
    public Guid GameId => Usage.Game.Id;
    public string GameTitle => Usage.Game.Title;
    public TimeSpan KnownPlaytime => Usage.FaceTime;
    public DateTimeOffset FirstRecordedAtUtc => Usage.FirstRecordedAtUtc;
    public DateTimeOffset LastRecordedAtUtc => Usage.LastRecordedAtUtc;
    public IReadOnlyList<string> Applications => Usage.Applications;
}

internal sealed record DesktopSrumHistoryPreview(
    string SourcePath,
    DateTimeOffset TrackingStartedAtUtc,
    int RawRowCount,
    IReadOnlyList<DesktopSrumHistoryCandidate> Candidates);

/// <summary>
/// Desktop-facing SRUM baseline workflow. Previewing is read-only. Persistence only happens
/// when the caller explicitly supplies selected normalized game candidates to ImportAsync.
/// Raw SRUM application rows never leave this service or get stored in GameHours SQLite.
/// </summary>
internal sealed class DesktopSrumHistoryService
{
    private readonly GameHoursDatabase _database;
    private readonly SqliteGameRepository _games;
    private readonly SqliteExecutableMappingRepository _mappings;
    private readonly SqliteTrackingStateRepository _trackingState;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteHistoricalEvidenceRepository _historicalEvidence;

    public DesktopSrumHistoryService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        _database = new GameHoursDatabase(databasePath);
        _games = new SqliteGameRepository(_database);
        _mappings = new SqliteExecutableMappingRepository(_database);
        _trackingState = new SqliteTrackingStateRepository(_database);
        _sessions = new SqliteSessionRepository(_database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(
            _database,
            _trackingState,
            _sessions);
    }

    public async Task<DesktopSrumHistoryPreview> PreviewAsync(
        CancellationToken cancellationToken = default)
    {
        var cutover = await _trackingState.GetTrackingStartedAtAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "GameHours debe haber iniciado el seguimiento al menos una vez antes de recuperar historial de Windows.");

        var currentSid = WindowsIdentity.GetCurrent().User?.Value;
        if (string.IsNullOrWhiteSpace(currentSid))
        {
            throw new InvalidOperationException("No se pudo determinar el usuario actual de Windows.");
        }

        var sourcePath = ResolveSrumPath();
        var reader = new SrumApplicationUsageReader();
        var rows = reader.Read(sourcePath, cutover, currentSid);

        var discovery = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
            {
                new SteamInstalledGameSource(),
                new EpicInstalledGameSource(),
                new GogInstalledGameSource()
            });
        var installedGames = await discovery.DiscoverAsync(cancellationToken);
        var resolver = new WindowsGameResolver(installedGames);
        var normalizer = new SrumGameUsageNormalizer(_mappings, _games, resolver);
        var normalized = await normalizer.NormalizeAsync(rows, cancellationToken);

        var candidates = new List<DesktopSrumHistoryCandidate>(normalized.Games.Count);
        foreach (var usage in normalized.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await _historicalEvidence.GetForGameAsync(
                usage.Game.Id,
                cancellationToken);
            var alreadyImported = existing.Any(item =>
                item.Source == HistoricalSource.Srum &&
                item.Kind == EvidenceKind.Baseline);
            candidates.Add(new DesktopSrumHistoryCandidate(usage, alreadyImported));
        }

        return new DesktopSrumHistoryPreview(
            sourcePath,
            cutover,
            rows.Count,
            candidates
                .OrderByDescending(candidate => candidate.KnownPlaytime)
                .ThenBy(candidate => candidate.GameTitle, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<SrumBaselineImportResult> ImportAsync(
        IEnumerable<DesktopSrumHistoryCandidate> selectedCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCandidates);
        var selected = selectedCandidates
            .Where(candidate => !candidate.AlreadyImported)
            .GroupBy(candidate => candidate.GameId)
            .Select(group => group.First().Usage)
            .ToArray();
        if (selected.Length == 0)
        {
            return new SrumBaselineImportResult(Array.Empty<SrumBaselineImportItem>());
        }

        var cutover = await _trackingState.GetTrackingStartedAtAsync(cancellationToken)
            ?? throw new InvalidOperationException(
                "GameHours no tiene un punto de inicio de seguimiento para proteger la línea temporal.");

        var importer = new SrumBaselineImporter(_games, _historicalEvidence);
        return await importer.ImportAsync(selected, cutover, cancellationToken);
    }

    private static string ResolveSrumPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("GAMEHOURS_SRUM_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim());
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "sru",
            "SRUDB.dat");
    }
}
