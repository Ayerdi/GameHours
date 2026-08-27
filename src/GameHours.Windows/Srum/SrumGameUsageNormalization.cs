using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Srum;

public sealed record SrumGameUsageDecision(
    string Application,
    string? ResolvedPath,
    string Decision,
    Guid? GameId,
    string? GameTitle,
    DateTimeOffset RecordedAtUtc,
    TimeSpan FaceTime);

public sealed record SrumNormalizedGameUsage(
    TrackedGame Game,
    TimeSpan FaceTime,
    int SourceRows,
    int SelectedRows,
    DateTimeOffset FirstRecordedAtUtc,
    DateTimeOffset LastRecordedAtUtc,
    IReadOnlyList<string> Applications);

public sealed record SrumGameUsageNormalizationResult(
    IReadOnlyList<SrumNormalizedGameUsage> Games,
    IReadOnlyList<SrumGameUsageDecision> Decisions);

public sealed class SrumGameUsageNormalizer
{
    private readonly IExecutableMappingRepository _mappings;
    private readonly IGameRepository _games;
    private readonly IGameResolver _resolver;
    private readonly WindowsDevicePathResolver _pathResolver;

    public SrumGameUsageNormalizer(
        IExecutableMappingRepository mappings,
        IGameRepository games,
        IGameResolver resolver,
        WindowsDevicePathResolver? pathResolver = null)
    {
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _pathResolver = pathResolver ?? new WindowsDevicePathResolver();
    }

    public async Task<SrumGameUsageNormalizationResult> NormalizeAsync(
        IEnumerable<SrumApplicationUsage> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var decisions = new List<SrumGameUsageDecision>();
        var accepted = new List<(TrackedGame Game, SrumApplicationUsage Row, string Path)>();
        var classificationByPath = new Dictionary<string, PathClassification>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var path = _pathResolver.Resolve(row.Application);
            if (string.IsNullOrWhiteSpace(path))
            {
                decisions.Add(ToDecision(row, null, "unresolved_device_path"));
                continue;
            }

            if (!classificationByPath.TryGetValue(path, out var classification))
            {
                classification = await ClassifyPathAsync(path, cancellationToken);
                classificationByPath[path] = classification;
            }

            if (classification.Game is { } game)
            {
                accepted.Add((game, row, path));
            }

            decisions.Add(ToDecision(
                row,
                path,
                classification.Decision,
                classification.Game));
        }

        var normalized = accepted
            .GroupBy(item => item.Game.Id)
            .Select(group =>
            {
                var game = group.First().Game;
                var sourceRows = group.Count();

                // Multiple processes for the same game can report FaceTime in the same SRUM
                // sample bucket. Taking the maximum per timestamp is conservative and avoids
                // double-counting launcher/root/child process overlap while still allowing
                // different executable paths to contribute at different times.
                var selected = group
                    .GroupBy(item => item.Row.RecordedAtUtc)
                    .Select(bucket => bucket
                        .OrderByDescending(item => item.Row.FaceTime)
                        .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
                        .First())
                    .ToArray();

                var totalTicks = selected.Aggregate(
                    0L,
                    (current, item) => checked(current + item.Row.FaceTime.Ticks));

                return new SrumNormalizedGameUsage(
                    game,
                    TimeSpan.FromTicks(totalTicks),
                    sourceRows,
                    selected.Length,
                    selected.Min(item => item.Row.RecordedAtUtc),
                    selected.Max(item => item.Row.RecordedAtUtc),
                    selected
                        .Select(item => item.Path)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                        .ToArray());
            })
            .OrderByDescending(item => item.FaceTime)
            .ThenBy(item => item.Game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new SrumGameUsageNormalizationResult(normalized, decisions);
    }

    private async Task<PathClassification> ClassifyPathAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (WindowsGameResolver.IsHelperExecutable(path))
        {
            return new PathClassification(null, "helper_executable");
        }

        var exactMapping = await _mappings.FindByPathAsync(path, cancellationToken);
        if (exactMapping is not null)
        {
            if (exactMapping.IsHelper)
            {
                return new PathClassification(null, "mapped_helper");
            }

            var mappedGame = await _games.GetByIdAsync(exactMapping.GameId, cancellationToken);
            return mappedGame is null
                ? new PathClassification(null, "mapping_game_missing")
                : new PathClassification(mappedGame, "accepted_exact_mapping");
        }

        var resolution = await _resolver.ResolveAsync(
            new ProcessSnapshot(
                0,
                Path.GetFileNameWithoutExtension(path),
                path,
                null),
            cancellationToken);

        if (resolution.Game is null || resolution.IsHelper || resolution.Confidence < 0.80)
        {
            return new PathClassification(
                null,
                resolution.IsHelper ? "helper_resolution" : resolution.Method);
        }

        var canonical = await _games.GetByTitleAsync(resolution.Game.Title, cancellationToken)
            ?? await _games.GetByIdAsync(resolution.Game.Id, cancellationToken)
            ?? resolution.Game;

        return new PathClassification(canonical, $"accepted_{resolution.Method}");
    }

    private static SrumGameUsageDecision ToDecision(
        SrumApplicationUsage row,
        string? resolvedPath,
        string decision,
        TrackedGame? game = null)
    {
        return new SrumGameUsageDecision(
            row.Application,
            resolvedPath,
            decision,
            game?.Id,
            game?.Title,
            row.RecordedAtUtc,
            row.FaceTime);
    }

    private sealed record PathClassification(TrackedGame? Game, string Decision);
}
