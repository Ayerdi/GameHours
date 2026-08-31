using System.Security.Principal;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Srum;

namespace GameHours.Desktop;

internal sealed record DesktopSrumHistoryCandidate(
    TrackedGame Game,
    EvidenceKind Kind,
    TimeSpan KnownPlaytime,
    DateTimeOffset FirstRecordedAtUtc,
    DateTimeOffset LastRecordedAtUtc,
    IReadOnlyList<string> Applications,
    IReadOnlyList<HistoricalEvidence> EvidenceItems,
    bool AlreadyImported)
{
    public Guid GameId => Game.Id;
    public string GameTitle => Game.Title;
    public string CandidateKey => $"{(int)Kind}:{Game.Id:D}";
}

internal sealed record DesktopSrumHistoryPreview(
    string SourcePath,
    DateTimeOffset TrackingStartedAtUtc,
    int RawRowCount,
    int BaselineRowCount,
    int GapRowCount,
    IReadOnlyList<DesktopSrumHistoryCandidate> Candidates);

internal sealed record DesktopSrumHistoryImportResult(
    int AddedCount,
    int SkippedCount,
    IReadOnlySet<string> CompletedCandidateKeys);

/// <summary>
/// Desktop-facing SRUM recovery workflow. Previewing is read-only. It keeps the original
/// pre-cutover baseline separate from conservative post-cutover gap recovery. Raw SRUM rows never
/// leave this service or get stored in GameHours SQLite.
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

        var sourcePath = SrumSourcePathResolver.Resolve();
        var reader = new SrumApplicationUsageReader();
        var rows = reader.Read(sourcePath, userSid: currentSid);
        var baselineRows = rows
            .Where(row => row.RecordedAtUtc <= cutover)
            .ToArray();
        var gapRows = rows
            .Where(row => row.RecordedAtUtc > cutover)
            .ToArray();

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

        var baselineNormalization = await normalizer.NormalizeAsync(
            baselineRows,
            cancellationToken);
        var gapNormalization = await normalizer.NormalizeAsync(
            gapRows,
            cancellationToken);

        var candidates = new List<DesktopSrumHistoryCandidate>();
        foreach (var usage in baselineNormalization.Games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var evidence = SrumBaselineEvidenceFactory.Create(
                usage.Game.Id,
                usage.FirstRecordedAtUtc,
                usage.LastRecordedAtUtc,
                usage.FaceTime,
                cutover);
            var existing = await _historicalEvidence.GetForGameAsync(
                usage.Game.Id,
                cancellationToken);
            var alreadyImported = existing.Any(item => item.Id == evidence.Id);

            candidates.Add(new DesktopSrumHistoryCandidate(
                usage.Game,
                EvidenceKind.Baseline,
                usage.FaceTime,
                evidence.PeriodStartUtc,
                evidence.PeriodEndUtc,
                usage.Applications,
                new[] { evidence },
                alreadyImported));
        }

        candidates.AddRange(await BuildGapCandidatesAsync(
            gapNormalization,
            cutover,
            cancellationToken));

        return new DesktopSrumHistoryPreview(
            sourcePath,
            cutover,
            rows.Count,
            baselineRows.Length,
            gapRows.Length,
            candidates
                .OrderBy(candidate => candidate.Kind is EvidenceKind.GapRecovery ? 0 : 1)
                .ThenByDescending(candidate => candidate.KnownPlaytime)
                .ThenBy(candidate => candidate.GameTitle, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    public async Task<DesktopSrumHistoryImportResult> ImportAsync(
        IEnumerable<DesktopSrumHistoryCandidate> selectedCandidates,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedCandidates);
        var selected = selectedCandidates
            .Where(candidate => !candidate.AlreadyImported)
            .GroupBy(candidate => candidate.CandidateKey, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();

        var addedCount = 0;
        var skippedCount = 0;
        var completed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _games.UpsertAsync(candidate.Game, cancellationToken);
            var candidateComplete = true;

            foreach (var evidence in candidate.EvidenceItems)
            {
                try
                {
                    if (await _historicalEvidence.AddAsync(evidence, cancellationToken))
                    {
                        addedCount++;
                    }
                }
                catch (TimelineConflictException)
                {
                    // Revalidate at write time. A measured session may have appeared after preview
                    // or another recovery may already own the interval. Never force an overlap.
                    skippedCount++;
                    candidateComplete = false;
                }
            }

            if (candidateComplete)
            {
                completed.Add(candidate.CandidateKey);
            }
        }

        return new DesktopSrumHistoryImportResult(
            addedCount,
            skippedCount,
            completed);
    }

    private async Task<IReadOnlyList<DesktopSrumHistoryCandidate>> BuildGapCandidatesAsync(
        SrumGameUsageNormalizationResult normalization,
        DateTimeOffset cutover,
        CancellationToken cancellationToken)
    {
        var accepted = normalization.Decisions
            .Where(decision =>
                decision.GameId is not null &&
                decision.ResolvedPath is not null &&
                decision.Decision.StartsWith("accepted_", StringComparison.Ordinal))
            .GroupBy(decision => decision.GameId!.Value)
            .ToArray();

        var results = new List<DesktopSrumHistoryCandidate>();
        foreach (var gameGroup in accepted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var game = await _games.GetByIdAsync(gameGroup.Key, cancellationToken);
            if (game is null)
            {
                // Gap recovery is deliberately limited to canonical games already known by
                // GameHours. It must not create games from historical evidence alone.
                continue;
            }

            var existing = await _historicalEvidence.GetForGameAsync(
                game.Id,
                cancellationToken);
            var planned = new List<HistoricalEvidence>();

            foreach (var bucket in gameGroup
                         .GroupBy(decision => decision.RecordedAtUtc)
                         .OrderBy(group => group.Key))
            {
                var sample = bucket
                    .OrderByDescending(decision => decision.FaceTime)
                    .ThenBy(decision => decision.ResolvedPath, StringComparer.OrdinalIgnoreCase)
                    .First();

                HistoricalEvidence evidence;
                try
                {
                    evidence = SrumGapRecoveryEvidenceFactory.Create(
                        game.Id,
                        sample.RecordedAtUtc,
                        sample.FaceTime,
                        cutover);
                }
                catch (TimelineConflictException)
                {
                    continue;
                }

                if (existing.Any(item => item.Id == evidence.Id))
                {
                    continue;
                }

                if (await _sessions.HasOverlapAsync(
                        game.Id,
                        evidence.PeriodStartUtc,
                        evidence.PeriodEndUtc,
                        cancellationToken))
                {
                    continue;
                }

                if (existing.Any(item => PlaytimeTimelineRules.Overlaps(
                        item.PeriodStartUtc,
                        item.PeriodEndUtc,
                        evidence.PeriodStartUtc,
                        evidence.PeriodEndUtc)) ||
                    planned.Any(item => PlaytimeTimelineRules.Overlaps(
                        item.PeriodStartUtc,
                        item.PeriodEndUtc,
                        evidence.PeriodStartUtc,
                        evidence.PeriodEndUtc)))
                {
                    continue;
                }

                planned.Add(evidence);
            }

            if (planned.Count == 0)
            {
                continue;
            }

            var totalTicks = planned.Aggregate(
                0L,
                (current, evidence) => checked(current + evidence.Duration.Ticks));
            var applications = gameGroup
                .Select(decision => decision.ResolvedPath!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            results.Add(new DesktopSrumHistoryCandidate(
                game,
                EvidenceKind.GapRecovery,
                TimeSpan.FromTicks(totalTicks),
                planned.Min(item => item.PeriodStartUtc),
                planned.Max(item => item.PeriodEndUtc),
                applications,
                planned,
                AlreadyImported: false));
        }

        return results;
    }
}
