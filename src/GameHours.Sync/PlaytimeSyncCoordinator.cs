using GameHours.Core.Domain;
using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public sealed record PlaytimeSyncExecution(
    PlaytimeSyncResult Result,
    int SentSessions);

public sealed class PlaytimeSyncCoordinator
{
    private readonly ISyncClient _client;

    public PlaytimeSyncCoordinator(ISyncClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<PlaytimeSyncExecution> SyncMeasuredSessionsAsync(
        DateTimeOffset trackingStartedAtUtc,
        IReadOnlyList<PlaySession> sessions,
        CancellationToken cancellationToken = default)
    {
        var batch = PlaytimeSyncBatchBuilder.BuildMeasuredSessions(
            trackingStartedAtUtc,
            sessions);

        var result = await _client.SyncPlaytimeAsync(batch, cancellationToken);
        return new PlaytimeSyncExecution(
            result,
            batch.Sessions.Count);
    }
}
