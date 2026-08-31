namespace GameHours.Core.Domain;

/// <summary>
/// Identifies the kind of source that proved an achievement unlock independently from the
/// platform/emulator achievement state. The model is intentionally positive-only: the absence
/// of evidence never means that an achievement is locked.
/// </summary>
public enum AchievementEvidenceOrigin
{
    SaveGame = 1,
    Runtime = 2,
    Imported = 3
}

/// <summary>
/// Auditable proof that one achievement was unlocked. Evidence providers may only emit this
/// type when their game-specific rule is strong enough to prove the unlock. They must never
/// emit negative evidence for achievements they cannot prove.
/// </summary>
public sealed record ConfirmedAchievementUnlockEvidence
{
    public Guid GameId { get; }
    public string ApiName { get; }
    public AchievementEvidenceOrigin Origin { get; }
    public string Provider { get; }
    public string RuleId { get; }
    public int RuleVersion { get; }
    public string? SourcePath { get; }
    public string? SourceFingerprint { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public string Detail { get; }

    public ConfirmedAchievementUnlockEvidence(
        Guid gameId,
        string apiName,
        AchievementEvidenceOrigin origin,
        string provider,
        string ruleId,
        int ruleVersion,
        string? sourcePath,
        string? sourceFingerprint,
        DateTimeOffset observedAtUtc,
        string detail)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        if (string.IsNullOrWhiteSpace(apiName))
        {
            throw new ArgumentException("Achievement API name cannot be empty.", nameof(apiName));
        }

        if (!Enum.IsDefined(origin))
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown achievement evidence origin.");
        }

        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("Evidence provider cannot be empty.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(ruleId))
        {
            throw new ArgumentException("Evidence rule id cannot be empty.", nameof(ruleId));
        }

        if (ruleVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ruleVersion), "Evidence rule version must be positive.");
        }

        if (string.IsNullOrWhiteSpace(detail))
        {
            throw new ArgumentException("Evidence detail cannot be empty.", nameof(detail));
        }

        GameId = gameId;
        ApiName = apiName.Trim();
        Origin = origin;
        Provider = provider.Trim();
        RuleId = ruleId.Trim();
        RuleVersion = ruleVersion;
        SourcePath = NormalizeOptional(sourcePath);
        SourceFingerprint = NormalizeOptional(sourceFingerprint);
        ObservedAtUtc = observedAtUtc.ToUniversalTime();
        Detail = detail.Trim();
    }

    private static string? NormalizeOptional(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
