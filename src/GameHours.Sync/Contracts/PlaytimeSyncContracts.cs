namespace GameHours.Sync.Contracts;

public sealed record PlaytimeSyncBatch(
    DateTimeOffset TrackingStartedAtUtc,
    IReadOnlyList<SessionSyncItem> Sessions,
    IReadOnlyList<HistoricalEvidenceSyncItem> Historical);

public sealed record SessionSyncItem(
    Guid ClientSessionId,
    long CatalogGameId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    string CaptureMethod,
    string Confidence);

public sealed record HistoricalEvidenceSyncItem(
    Guid ClientEvidenceId,
    long CatalogGameId,
    string Source,
    string EvidenceKind,
    string Metric,
    string Confidence,
    DateTimeOffset PeriodStartUtc,
    DateTimeOffset PeriodEndUtc,
    long DurationMilliseconds);

public sealed record PlaytimeSyncResult(
    int AcceptedSessions,
    int AcceptedHistorical,
    int DuplicateSessions,
    int DuplicateHistorical,
    IReadOnlyList<SyncRejection> Rejected);

public sealed record SyncRejection(
    string Kind,
    Guid? ClientId,
    string Code,
    string Message);
