namespace GameHours.Windows.Achievements;

public interface ILocalAchievementProvider
{
    string Name { get; }

    LocalAchievementSnapshot? TryRead(string executablePath);

    /// <summary>
    /// Structured read result for diagnostics and high-reliability callers. Existing providers
    /// keep their current snapshot contract while they are migrated to richer source-specific
    /// diagnostics; the default adapter deliberately reports unknown state coverage.
    /// </summary>
    AchievementReadResult TryReadDetailed(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AchievementReadResult.NoSource(Name, "Executable path is empty.");
        }

        try
        {
            var snapshot = TryRead(executablePath);
            return snapshot is null
                ? AchievementReadResult.NoSource(Name)
                : AchievementReadResult.Success(Name, snapshot);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return AchievementReadResult.Failure(
                Name,
                AchievementReadStatus.Failed,
                AchievementSourceHealth.Degraded,
                exception.Message);
        }
    }
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

    public LocalAchievementSnapshot? TryRead(string executablePath) =>
        TryReadDetailed(executablePath).Snapshot;

    public AchievementReadResult TryReadDetailed(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var diagnostics = new List<AchievementReadDiagnostic>();
        foreach (var provider in _providers)
        {
            var result = provider.TryReadDetailed(executablePath);
            if (result.IsSuccess)
            {
                return diagnostics.Count == 0
                    ? result
                    : result with
                    {
                        Health = AchievementSourceHealth.Degraded,
                        Diagnostics = diagnostics.Concat(result.Diagnostics).ToArray()
                    };
            }

            if (result.Status != AchievementReadStatus.NoSource)
            {
                diagnostics.AddRange(result.Diagnostics);
            }
        }

        return diagnostics.Count == 0
            ? AchievementReadResult.NoSource("Local achievement provider chain")
            : new AchievementReadResult(
                "Local achievement provider chain",
                AchievementReadStatus.Failed,
                AchievementSourceHealth.Degraded,
                AchievementStateCoverage.Unknown,
                Snapshot: null,
                diagnostics);
    }
}
