namespace GameHours.Windows.Achievements;

/// <summary>
/// Describes how much user-state information a local achievement source can prove.
/// Catalogue completeness is intentionally separate: a complete list of achievements does not
/// imply that the source knows the locked/unlocked state of every entry.
/// </summary>
public enum AchievementStateCoverage
{
    Unknown,
    UnlocksOnly,
    Complete
}

public enum AchievementReadStatus
{
    Success,
    NoSource,
    Unsupported,
    Invalid,
    Ambiguous,
    Failed
}

public enum AchievementSourceHealth
{
    Healthy,
    Degraded,
    Stale,
    Ambiguous,
    Invalid,
    Unsupported
}

public sealed record AchievementReadDiagnostic(
    AchievementReadStatus Status,
    string Provider,
    string? SourcePath,
    string Detail);

/// <summary>
/// Structured result for local achievement reads. This keeps "no source", malformed data and
/// successful-but-partial state distinct instead of collapsing every non-success into null.
/// </summary>
public sealed record AchievementReadResult(
    string Provider,
    AchievementReadStatus Status,
    AchievementSourceHealth Health,
    AchievementStateCoverage StateCoverage,
    LocalAchievementSnapshot? Snapshot,
    IReadOnlyList<AchievementReadDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == AchievementReadStatus.Success && Snapshot is not null;

    public static AchievementReadResult Success(
        string provider,
        LocalAchievementSnapshot snapshot,
        AchievementStateCoverage stateCoverage = AchievementStateCoverage.Unknown,
        AchievementSourceHealth health = AchievementSourceHealth.Healthy,
        IReadOnlyList<AchievementReadDiagnostic>? diagnostics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentNullException.ThrowIfNull(snapshot);

        return new AchievementReadResult(
            provider,
            AchievementReadStatus.Success,
            health,
            stateCoverage,
            snapshot,
            diagnostics ?? Array.Empty<AchievementReadDiagnostic>());
    }

    public static AchievementReadResult NoSource(
        string provider,
        string? detail = null,
        string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        var diagnostics = string.IsNullOrWhiteSpace(detail)
            ? Array.Empty<AchievementReadDiagnostic>()
            : new[]
            {
                new AchievementReadDiagnostic(
                    AchievementReadStatus.NoSource,
                    provider,
                    sourcePath,
                    detail)
            };

        return new AchievementReadResult(
            provider,
            AchievementReadStatus.NoSource,
            AchievementSourceHealth.Healthy,
            AchievementStateCoverage.Unknown,
            Snapshot: null,
            diagnostics);
    }

    public static AchievementReadResult Failure(
        string provider,
        AchievementReadStatus status,
        AchievementSourceHealth health,
        string detail,
        string? sourcePath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);
        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        if (status is AchievementReadStatus.Success or AchievementReadStatus.NoSource)
        {
            throw new ArgumentOutOfRangeException(nameof(status), status, "Failure status required.");
        }

        return new AchievementReadResult(
            provider,
            status,
            health,
            AchievementStateCoverage.Unknown,
            Snapshot: null,
            new[]
            {
                new AchievementReadDiagnostic(status, provider, sourcePath, detail)
            });
    }
}
