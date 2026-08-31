namespace GameHours.Windows.Achievements;

public sealed class SteamLibraryCacheLocalAchievementProvider : ILocalAchievementProvider
{
    private readonly SteamLibraryCacheAchievementReader _reader = new();

    public string Name => "Steam local cache";

    public LocalAchievementSnapshot? TryRead(string executablePath) =>
        _reader.TryRead(executablePath) is { } snapshot
            ? snapshot with { IsCatalogueComplete = false }
            : null;
}

public sealed class LegacyLocalAchievementStateProvider : ILocalAchievementProvider
{
    private readonly LocalAchievementSourceLocator _locator = new();
    private readonly PartialAchievementStateReader _reader = new();

    public string Name => "Local Steam-compatible state";

    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        IReadOnlyList<LocalAchievementSourceCandidate> candidates;
        try
        {
            candidates = _locator.Locate(executablePath)
                .Where(item => _reader.Supports(item.Kind))
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            var snapshot = _reader.TryRead(candidate);
            if (snapshot?.UnlockedCount > 0)
            {
                return snapshot with { IsCatalogueComplete = false };
            }
        }

        // A partial state file with zero parsed unlocks is not authoritative enough to stop
        // the provider chain: another local source (for example Steam librarycache) may know more.
        return null;
    }
}
