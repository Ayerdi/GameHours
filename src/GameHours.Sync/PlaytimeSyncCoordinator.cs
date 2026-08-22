using GameHours.Core.Domain;
using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public sealed record PlaytimeSyncExecution(
    PlaytimeSyncResult Result,
    int SentSessions,
    IReadOnlyList<Guid> UnmappedGameIds);

public sealed class PlaytimeSyncCoordinator
{
    private readonly ISyncClient _client;

    public PlaytimeSyncCoordinator(ISyncClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    public async Task<PlaytimeSyncExecution> SyncMeasuredSessionsAsync(
        DateTimeOffset trackingStartedAtUtc,
        IReadOnlyList<PlaySession> sessions,
        IReadOnlyDictionary<Guid, long> catalogGameIds,
        CancellationToken cancellationToken = default)
    {
        var build = PlaytimeSyncBatchBuilder.BuildMeasuredSessions(
            trackingStartedAtUtc,
            sessions,
            catalogGameIds);

        var result = await _client.SyncPlaytimeAsync(build.Batch, cancellationToken);
        return new PlaytimeSyncExecution(
            result,
            build.Batch.Sessions.Count,
            build.UnmappedGameIds);
    }
}
