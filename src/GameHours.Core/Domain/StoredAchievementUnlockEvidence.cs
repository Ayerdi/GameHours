namespace GameHours.Core.Domain;

/// <summary>
/// Durable positive proof of an achievement unlock. SourcePath is local provenance and may be
/// null when a provider has no file-backed source. First/last observation times describe when
/// GameHours observed the proof; they are not fabricated achievement unlock timestamps.
/// </summary>
public sealed record StoredAchievementUnlockEvidence(
    Guid GameId,
    string ApiName,
    AchievementEvidenceOrigin Origin,
    string Provider,
    string RuleId,
    int RuleVersion,
    string? SourcePath,
    string? SourceFingerprint,
    string Detail,
    DateTimeOffset FirstObservedAtUtc,
    DateTimeOffset LastObservedAtUtc);
