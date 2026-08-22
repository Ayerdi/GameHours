using System.Text.Json.Serialization;

namespace GameHours.Sync.Contracts;

public sealed record PlaytimeSyncBatch(
    [property: JsonPropertyName("tracking_started_at")] DateTimeOffset TrackingStartedAtUtc,
    IReadOnlyList<SessionSyncItem> Sessions,
    IReadOnlyList<HistoricalEvidenceSyncItem> Historical);

public sealed record SessionSyncItem(
    Guid ClientSessionId,
    [property: JsonPropertyName("catalogo_juego_id")] long CatalogGameId,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAtUtc,
    [property: JsonPropertyName("ended_at")] DateTimeOffset EndedAtUtc,
    string CaptureMethod,
    string Confidence);

public sealed record HistoricalEvidenceSyncItem(
    Guid ClientEvidenceId,
    [property: JsonPropertyName("catalogo_juego_id")] long CatalogGameId,
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
