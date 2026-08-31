using GameHours.Core.Domain;
using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public static class PlaytimeSyncBatchBuilder
{
    public static PlaytimeSyncBatch BuildMeasuredSessions(
        DateTimeOffset trackingStartedAtUtc,
        IReadOnlyList<PlaySession> sessions)
    {
        ArgumentNullException.ThrowIfNull(sessions);

        var normalizedCutover = trackingStartedAtUtc.ToUniversalTime();
        var items = new List<SessionSyncItem>(sessions.Count);

        foreach (var session in sessions)
        {
            if (session.StartedAtUtc < normalizedCutover)
            {
                throw new InvalidOperationException(
                    $"Measured session {session.Id:D} starts before the tracking cutover.");
            }

            if (session.GameId == Guid.Empty)
            {
                throw new InvalidOperationException(
                    $"Measured session {session.Id:D} has no GameHours game id.");
            }

            items.Add(new SessionSyncItem(
                session.Id,
                session.GameId,
                session.StartedAtUtc,
                session.EndedAtUtc,
                SerializeCaptureMethod(session.CaptureMethod),
                SerializeConfidence(session.Confidence)));
        }

        return new PlaytimeSyncBatch(
            normalizedCutover,
            items,
            Array.Empty<HistoricalEvidenceSyncItem>());
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
