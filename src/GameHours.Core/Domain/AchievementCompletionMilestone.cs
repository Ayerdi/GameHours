namespace GameHours.Core.Domain;

/// <summary>
/// Durable moment at which a complete, non-empty achievement catalogue became fully unlocked.
/// CompletedAtUtc uses the latest best-known unlock occurrence among the catalogue. When that
/// final occurrence has no source timestamp, IsObservedTimeFallback makes the approximation
/// explicit instead of presenting GameHours' observation time as an exact unlock time.
/// </summary>
public sealed record AchievementCompletionMilestone(
    Guid GameId,
    string GameTitle,
    DateTimeOffset CompletedAtUtc,
    bool IsObservedTimeFallback,
    string Source);
