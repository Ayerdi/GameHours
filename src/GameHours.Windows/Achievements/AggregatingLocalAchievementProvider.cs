namespace GameHours.Windows.Achievements;

/// <summary>
/// Builds the richest local achievement snapshot available for a game.
/// A complete local catalogue is enriched with unlock state from any compatible local source.
/// If no complete catalogue exists, unlocked achievements from partial state sources are unioned.
/// No network access is performed.
/// </summary>
public sealed class AggregatingLocalAchievementProvider : ILocalAchievementProvider
{
    private readonly GseAchievementReader _catalogueReader = new();
    private readonly LocalAchievementSourceLocator _locator = new();
    private readonly PartialAchievementStateReader _partialReader = new();
    private readonly SteamLibraryCacheAchievementReader _steamCacheReader = new();

    public string Name => "Aggregated local achievements";

    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var catalogue = _catalogueReader.TryRead(executablePath);
            var states = ReadPartialStates(executablePath).ToList();

            var steamState = _steamCacheReader.TryRead(executablePath);
            if (steamState is not null)
            {
                states.Add(steamState with { IsCatalogueComplete = false });
            }

            if (catalogue is not null)
            {
                // The GSE reader can already include its own state. Keeping the catalogue snapshot
                // in the merge preserves that state while allowing other local formats to fill gaps.
                var allStates = new List<LocalAchievementSnapshot> { catalogue };
                allStates.AddRange(states);
                return LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, allStates);
            }

            return LocalAchievementSnapshotMerger.MergePartialStates(states);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    private IEnumerable<LocalAchievementSnapshot> ReadPartialStates(string executablePath)
    {
        IReadOnlyList<LocalAchievementSourceCandidate> candidates;
        try
        {
            candidates = _locator.Locate(executablePath)
                .Where(candidate => _partialReader.Supports(candidate.Kind))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            yield break;
        }

        foreach (var candidate in candidates)
        {
            var snapshot = _partialReader.TryRead(candidate);
            if (snapshot?.UnlockedCount > 0)
            {
                yield return snapshot with { IsCatalogueComplete = false };
            }
        }
    }
}
