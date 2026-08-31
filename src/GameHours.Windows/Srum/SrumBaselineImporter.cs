using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;

namespace GameHours.Windows.Srum;

public sealed record SrumBaselineImportItem(
    TrackedGame Game,
    HistoricalEvidence Evidence,
    bool Added);

public sealed record SrumBaselineImportResult(
    IReadOnlyList<SrumBaselineImportItem> Items)
{
    public int AddedCount => Items.Count(item => item.Added);
    public int ExistingCount => Items.Count - AddedCount;
}

public sealed class SrumBaselineImporter
{
    private readonly IGameRepository _games;
    private readonly IHistoricalEvidenceRepository _evidence;

    public SrumBaselineImporter(
        IGameRepository games,
        IHistoricalEvidenceRepository evidence)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
    }

    public async Task<SrumBaselineImportResult> ImportAsync(
        IEnumerable<SrumNormalizedGameUsage> normalizedGames,
        DateTimeOffset trackingStartedAtUtc,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(normalizedGames);

        var items = new List<SrumBaselineImportItem>();
        foreach (var usage in normalizedGames)
        {
            cancellationToken.ThrowIfCancellationRequested();

            await _games.UpsertAsync(usage.Game, cancellationToken);

            var evidence = SrumBaselineEvidenceFactory.Create(
                usage.Game.Id,
                usage.FirstRecordedAtUtc,
                usage.LastRecordedAtUtc,
                usage.FaceTime,
                trackingStartedAtUtc);

            var added = await _evidence.AddAsync(evidence, cancellationToken);
            items.Add(new SrumBaselineImportItem(usage.Game, evidence, added));
        }

        return new SrumBaselineImportResult(items);
    }
}
