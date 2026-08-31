using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// Context shared by game-specific evidence providers. Stable identifiers such as PlatformAppId
/// should be preferred over display-title matching when deciding whether a provider applies.
/// </summary>
public sealed record AchievementEvidenceRequest(
    Guid GameId,
    string GameTitle,
    string? ExecutablePath,
    string? PlatformAppId,
    DateTimeOffset ObservedAtUtc)
{
    public AchievementEvidenceRequest Normalize()
    {
        if (GameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(GameId));
        }

        if (string.IsNullOrWhiteSpace(GameTitle))
        {
            throw new ArgumentException("Game title cannot be empty.", nameof(GameTitle));
        }

        return this with
        {
            GameTitle = GameTitle.Trim(),
            ExecutablePath = NormalizeOptional(ExecutablePath),
            PlatformAppId = NormalizeOptional(PlatformAppId),
            ObservedAtUtc = ObservedAtUtc.ToUniversalTime()
        };
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

public enum AchievementEvidenceReadStatus
{
    Success,
    NotApplicable,
    NoEvidence,
    Failed
}

public sealed record AchievementEvidenceDiagnostic(
    string Provider,
    string Detail,
    string? SourcePath = null);

/// <summary>
/// Result of one provider read. Success contains positive proofs only. NoEvidence means the
/// provider applies but the inspected state could not prove any unlock; it must never be
/// interpreted as proof that achievements are locked. Applicable rule-based providers also
/// expose the rule revisions they currently declare active so persisted evidence can be
/// projected without callers knowing provider internals.
/// </summary>
public sealed record AchievementEvidenceReadResult(
    string Provider,
    AchievementEvidenceReadStatus Status,
    IReadOnlyList<ConfirmedAchievementUnlockEvidence> Evidence,
    IReadOnlyList<AchievementEvidenceDiagnostic> Diagnostics)
{
    public bool IsSuccess => Status == AchievementEvidenceReadStatus.Success;

    public IReadOnlyList<AchievementEvidenceRuleIdentity> ActiveRuleIdentities { get; init; } =
        Array.Empty<AchievementEvidenceRuleIdentity>();

    public static AchievementEvidenceReadResult Success(
        string provider,
        IReadOnlyList<ConfirmedAchievementUnlockEvidence> evidence)
    {
        ValidateProvider(provider);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            throw new ArgumentException("Successful evidence result must contain at least one proof.", nameof(evidence));
        }

        return new AchievementEvidenceReadResult(
            provider.Trim(),
            AchievementEvidenceReadStatus.Success,
            evidence,
            Array.Empty<AchievementEvidenceDiagnostic>());
    }

    public static AchievementEvidenceReadResult NotApplicable(string provider)
    {
        ValidateProvider(provider);
        return Empty(provider, AchievementEvidenceReadStatus.NotApplicable);
    }

    public static AchievementEvidenceReadResult NoEvidence(string provider)
    {
        ValidateProvider(provider);
        return Empty(provider, AchievementEvidenceReadStatus.NoEvidence);
    }

    public static AchievementEvidenceReadResult Failure(
        string provider,
        string detail,
        string? sourcePath = null)
    {
        ValidateProvider(provider);
        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("Evidence failure detail cannot be empty.", nameof(detail));
        }

        var normalizedProvider = provider.Trim();
        return new AchievementEvidenceReadResult(
            normalizedProvider,
            AchievementEvidenceReadStatus.Failed,
            Array.Empty<ConfirmedAchievementUnlockEvidence>(),
            new[]
            {
                new AchievementEvidenceDiagnostic(
                    normalizedProvider,
                    detail.Trim(),
                    string.IsNullOrWhiteSpace(sourcePath) ? null : sourcePath.Trim())
            });
    }

    private static AchievementEvidenceReadResult Empty(
        string provider,
        AchievementEvidenceReadStatus status) =>
        new(
            provider.Trim(),
            status,
            Array.Empty<ConfirmedAchievementUnlockEvidence>(),
            Array.Empty<AchievementEvidenceDiagnostic>());

    private static void ValidateProvider(string provider)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Evidence provider cannot be empty.", nameof(provider));
        }
    }
}

/// <summary>
/// Extension point for game-specific achievement recovery. Providers are expected to perform a
/// cheap applicability check before expensive parsing and to remain read-only with respect to
/// game saves and platform/emulator achievement state.
/// </summary>
public interface IAchievementUnlockEvidenceProvider
{
    string Name { get; }

    Task<AchievementEvidenceReadResult> ReadAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default);
}
