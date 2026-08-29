namespace GameHours.Windows.Achievements;

/// <summary>
/// Builds the richest local achievement snapshot available for a game.
/// Official Steam installations and Steam-compatible emulator saves are deliberately
/// isolated so state from two installations sharing an AppID is never mixed.
/// No network access is performed.
/// </summary>
public sealed class AggregatingLocalAchievementProvider : ILocalAchievementProvider
{
    private readonly GseAchievementReader _gseCatalogueReader = new();
    private readonly GseRuntimeAchievementStateReader _gseStateReader = new();
    private readonly SteamLocalStatsAchievementReader _steamStatsReader = new();
    private readonly SteamAchievementArtworkEnricher _steamArtworkEnricher = new();
    private readonly SteamAchievementMetadataCache _steamMetadataCache = new();
    private readonly SteamLibraryCacheAchievementReader _steamCacheReader = new();
    private readonly LocalAchievementSourceLocator _locator = new();
    private readonly PartialAchievementStateReader _partialReader = new();

    public string Name => "Aggregated local achievements";

    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return SteamLocalInstallation.TryResolve(executablePath) is not null
                ? ReadOfficialSteam(executablePath)
                : ReadNonSteamLocal(executablePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    private LocalAchievementSnapshot? ReadOfficialSteam(string executablePath)
    {
        var catalogue = _steamStatsReader.TryRead(executablePath);
        if (catalogue is not null)
        {
            catalogue = _steamArtworkEnricher.Enrich(catalogue);
        }

        var cacheState = _steamCacheReader.TryRead(executablePath) is { } cache
            ? cache with { IsCatalogueComplete = false }
            : null;

        if (catalogue is null)
        {
            return cacheState;
        }

        var states = new List<LocalAchievementSnapshot> { catalogue };
        if (cacheState is not null)
        {
            states.Add(cacheState);
        }

        return LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, states);
    }

    private LocalAchievementSnapshot? ReadNonSteamLocal(string executablePath)
    {
        var catalogue = AsCatalogueOnly(_gseCatalogueReader.TryRead(executablePath));
        var states = ReadEmulatorStates(executablePath).ToArray();

        LocalAchievementSnapshot? snapshot;
        if (catalogue is null)
        {
            snapshot = LocalAchievementSnapshotMerger.MergePartialStates(states);
        }
        else
        {
            var allStates = new List<LocalAchievementSnapshot> { catalogue };
            allStates.AddRange(states);
            snapshot = LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, allStates);
        }

        // The provider remains local-only: this reads a previously fetched metadata cache.
        // Emulator files still own unlock state, timestamps and progress; Steam contributes presentation only.
        return snapshot is null ? null : _steamMetadataCache.EnrichFromCache(snapshot);
    }

    private IEnumerable<LocalAchievementSnapshot> ReadEmulatorStates(string executablePath)
    {
        var gseState = _gseStateReader.TryRead(executablePath);
        if (gseState is not null)
        {
            yield return gseState;
        }

        IReadOnlyList<LocalAchievementSourceCandidate> candidates;
        try
        {
            candidates = _locator.Locate(executablePath)
                .Where(candidate =>
                    candidate.Kind is not LocalAchievementSourceKind.Goldberg &&
                    _partialReader.Supports(candidate.Kind))
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
            if (snapshot is not null)
            {
                // An empty but valid state file matters: it establishes a baseline before the
                // first unlock and prevents that first future unlock from being treated as history.
                yield return snapshot with { IsCatalogueComplete = false };
            }
        }
    }

    private static LocalAchievementSnapshot? AsCatalogueOnly(LocalAchievementSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return snapshot with
        {
            StatePath = null,
            Achievements = snapshot.Achievements
                .Select(achievement => achievement with
                {
                    IsUnlocked = false,
                    UnlockedAtUtc = null,
                    Progress = null
                })
                .ToArray()
        };
    }
}
