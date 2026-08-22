using System.Collections.Concurrent;
using System.Text.Json;
using GameHours.Sync.Contracts;

namespace GameHours.Sync;

public sealed class LocalFileSyncClient : ISyncClient
{
    private const string StateFileName = "sync-state.json";
    private const string ReceiptsFileName = "sync-receipts.jsonl";
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> DirectoryGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _directory;
    private readonly SemaphoreSlim _gate;

    public LocalFileSyncClient(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("Sync directory cannot be empty.", nameof(directory));
        }

        _directory = Path.GetFullPath(directory);
        _gate = DirectoryGates.GetOrAdd(_directory, static _ => new SemaphoreSlim(1, 1));
    }

    public async Task<PlaytimeSyncResult> SyncPlaytimeAsync(
        PlaytimeSyncBatch batch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_directory);
            var state = await LoadStateAsync(cancellationToken);
            var rejected = new List<SyncRejection>();
            var acceptedSessions = 0;
            var duplicateSessions = 0;
            var acceptedHistorical = 0;
            var duplicateHistorical = 0;

            foreach (var session in batch.Sessions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = session.ClientSessionId.ToString("D");

                if (state.Sessions.TryGetValue(key, out var existing))
                {
                    if (existing == session)
                    {
                        duplicateSessions++;
                    }
                    else
                    {
                        rejected.Add(new SyncRejection(
                            "session",
                            session.ClientSessionId,
                            "idempotency_conflict",
                            "The client session id was already accepted with different data."));
                    }
                    continue;
                }

                var rejection = ValidateSession(session);
                if (rejection is not null)
                {
                    rejected.Add(rejection);
                    continue;
                }

                state.Sessions.Add(key, session);
                acceptedSessions++;
            }

            foreach (var evidence in batch.Historical)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var key = evidence.ClientEvidenceId.ToString("D");

                if (state.Historical.TryGetValue(key, out var existing))
                {
                    if (existing == evidence)
                    {
                        duplicateHistorical++;
                    }
                    else
                    {
                        rejected.Add(new SyncRejection(
                            "historical",
                            evidence.ClientEvidenceId,
                            "idempotency_conflict",
                            "The client evidence id was already accepted with different data."));
                    }
                    continue;
                }

                var rejection = ValidateHistorical(evidence);
                if (rejection is not null)
                {
                    rejected.Add(rejection);
                    continue;
                }

                state.Historical.Add(key, evidence);
                acceptedHistorical++;
            }

            var result = new PlaytimeSyncResult(
                acceptedSessions,
                acceptedHistorical,
                duplicateSessions,
                duplicateHistorical,
                rejected);

            await SaveStateAsync(state, cancellationToken);
            await AppendReceiptAsync(batch, result, cancellationToken);
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<LocalSyncState> LoadStateAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, StateFileName);
        if (!File.Exists(path))
        {
            return new LocalSyncState();
        }

        await using var stream = File.OpenRead(path);
        var state = await JsonSerializer.DeserializeAsync<LocalSyncState>(
            stream,
            SyncJson.SerializerOptions,
            cancellationToken);

        if (state?.Sessions is null || state.Historical is null)
        {
            throw new InvalidDataException("Local sync state is invalid; refusing to reset idempotency state silently.");
        }

        return state;
    }

    private async Task SaveStateAsync(LocalSyncState state, CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, StateFileName);
        var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(temporaryPath))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    state,
                    SyncJson.SerializerOptions,
                    cancellationToken);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task AppendReceiptAsync(
        PlaytimeSyncBatch batch,
        PlaytimeSyncResult result,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, ReceiptsFileName);
        var receipt = new LocalSyncReceipt(DateTimeOffset.UtcNow, batch, result);
        var line = JsonSerializer.Serialize(receipt, SyncJson.SerializerOptions);
        await File.AppendAllTextAsync(path, line + Environment.NewLine, cancellationToken);
    }

    private static SyncRejection? ValidateSession(SessionSyncItem session)
    {
        if (session.ClientSessionId == Guid.Empty)
        {
            return new SyncRejection("session", null, "invalid_client_id", "Client session id cannot be empty.");
        }
        if (session.CatalogGameId <= 0)
        {
            return new SyncRejection("session", session.ClientSessionId, "invalid_catalog_game_id", "Catalog game id must be positive.");
        }
        if (session.EndedAtUtc <= session.StartedAtUtc)
        {
            return new SyncRejection("session", session.ClientSessionId, "invalid_interval", "Session end must be after session start.");
        }
        if (string.IsNullOrWhiteSpace(session.CaptureMethod) || string.IsNullOrWhiteSpace(session.Confidence))
        {
            return new SyncRejection("session", session.ClientSessionId, "invalid_metadata", "Capture method and confidence are required.");
        }
        return null;
    }

    private static SyncRejection? ValidateHistorical(HistoricalEvidenceSyncItem evidence)
    {
        if (evidence.ClientEvidenceId == Guid.Empty)
        {
            return new SyncRejection("historical", null, "invalid_client_id", "Client evidence id cannot be empty.");
        }
        if (evidence.CatalogGameId <= 0)
        {
            return new SyncRejection("historical", evidence.ClientEvidenceId, "invalid_catalog_game_id", "Catalog game id must be positive.");
        }
        if (evidence.PeriodEndUtc <= evidence.PeriodStartUtc || evidence.DurationMilliseconds < 0)
        {
            return new SyncRejection("historical", evidence.ClientEvidenceId, "invalid_interval", "Historical evidence interval or duration is invalid.");
        }
        return null;
    }

    private sealed class LocalSyncState
    {
        public Dictionary<string, SessionSyncItem> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, HistoricalEvidenceSyncItem> Historical { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed record LocalSyncReceipt(
        DateTimeOffset ReceivedAtUtc,
        PlaytimeSyncBatch Batch,
        PlaytimeSyncResult Result);
}
