namespace GameHours.Windows.Achievements;

public interface ILocalAchievementProvider
{
    string Name { get; }

    LocalAchievementSnapshot? TryRead(string executablePath);
}

public sealed class GseLocalAchievementProvider : ILocalAchievementProvider
{
    private readonly GseAchievementReader _reader = new();

    public string Name => "GSE/Goldberg local";

    public LocalAchievementSnapshot? TryRead(string executablePath) =>
        _reader.TryRead(executablePath);
}

public sealed class LocalAchievementProviderChain
{
    private readonly IReadOnlyList<ILocalAchievementProvider> _providers;

    public LocalAchievementProviderChain(IEnumerable<ILocalAchievementProvider> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);
        _providers = providers.ToArray();
    }

    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        foreach (var provider in _providers)
        {
            var snapshot = provider.TryRead(executablePath);
            if (snapshot is not null)
            {
                return snapshot;
            }
        }

        return null;
    }
}
