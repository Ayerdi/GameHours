using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public interface ISyncClient
{
    Task<PlaytimeSyncResult> SyncPlaytimeAsync(
        PlaytimeSyncBatch batch,
        CancellationToken cancellationToken = default);
}
