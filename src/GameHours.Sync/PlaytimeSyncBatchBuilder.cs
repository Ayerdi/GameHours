using GameHours.Core.Domain;
using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public sealed record PlaytimeSyncBuildResult(
    PlaytimeSyncBatch Batch,
    IReadOnlyList<Guid> UnmappedGameIds);

public static class PlaytimeSyncBatchBuilder
{
    public static PlaytimeSyncBuildResult BuildMeasuredSessions(
        DateTimeOffset trackingStartedAtUtc,
        IReadOnlyList<PlaySession> sessions,
        IReadOnlyDictionary<Guid, long> catalogGameIds)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        ArgumentNullException.ThrowIfNull(catalogGameIds);

        var normalizedCutover = trackingStartedAtUtc.ToUniversalTime();
        var items = new List<SessionSyncItem>(sessions.Count);
        var unmapped = new HashSet<Guid>();

        foreach (var session in sessions)
        {
            if (session.StartedAtUtc < normalizedCutover)
            {
                throw new InvalidOperationException(
                    $"Measured session {session.Id:D} starts before the tracking cutover.");
            }

            if (!catalogGameIds.TryGetValue(session.GameId, out var catalogGameId) || catalogGameId <= 0)
            {
                unmapped.Add(session.GameId);
                continue;
            }

            items.Add(new SessionSyncItem(
                session.Id,
                catalogGameId,
                session.StartedAtUtc,
                session.EndedAtUtc,
                SerializeCaptureMethod(session.CaptureMethod),
                SerializeConfidence(session.Confidence)));
        }

        return new PlaytimeSyncBuildResult(
            new PlaytimeSyncBatch(
                normalizedCutover,
                items,
                Array.Empty<HistoricalEvidenceSyncItem>()),
            unmapped.OrderBy(id => id).ToArray());
    }

    private static string SerializeCaptureMethod(CaptureMethod captureMethod) => captureMethod switch
    {
        CaptureMethod.Etw => "etw",
        CaptureMethod.Wmi => "wmi",
        CaptureMethod.Reconciliation => "reconciliation",
        CaptureMethod.InitialSnapshot => "initial_snapshot",
        _ => throw new ArgumentOutOfRangeException(nameof(captureMethod), captureMethod, "Unsupported capture method.")
    };

    private static string SerializeConfidence(Confidence confidence) => confidence switch
    {
        Confidence.High => "high",
        Confidence.Exact => "exact",
        Confidence.Estimated => throw new InvalidOperationException("Measured sessions cannot be synchronized with estimated confidence."),
        _ => throw new ArgumentOutOfRangeException(nameof(confidence), confidence, "Unsupported confidence.")
    };
}
