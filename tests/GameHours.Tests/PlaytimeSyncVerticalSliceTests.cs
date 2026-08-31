using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using GameHours.Sync;
using GameHours.Sync.Contracts;

namespace GameHours.Tests;

public sealed class PlaytimeSyncVerticalSliceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "gamehours.db");
    private string SyncDirectory => Path.Combine(_directory, "sync-receiver");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task PersistedMeasuredSession_TravelsThroughNeutralSyncBoundary_Idempotently()
    {
        var database = new GameHoursDatabase(DatabasePath);
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var sessions = new SqliteSessionRepository(database);
        var tracking = new SqliteTrackingStateRepository(database);

        var game = new TrackedGame(Guid.NewGuid(), "Vertical Slice Game");
        await games.UpsertAsync(game);

        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        await tracking.GetOrSetTrackingStartedAtAsync(cutover);

        var session = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            cutover.AddMinutes(10),
            cutover.AddMinutes(42),
            CaptureMethod.Reconciliation,
            Confidence.High,
            "process-exit");
        Assert.True(await sessions.AddAsync(session));

        var persistedCutover = Assert.NotNull(await tracking.GetTrackingStartedAtAsync());
        var persistedSessions = await sessions.GetForGameAsync(game.Id);

        var firstCoordinator = new PlaytimeSyncCoordinator(new LocalFileSyncClient(SyncDirectory));
        var first = await firstCoordinator.SyncMeasuredSessionsAsync(
            persistedCutover,
            persistedSessions);

        Assert.Equal(1, first.SentSessions);
        Assert.Equal(1, first.Result.AcceptedSessions);
        Assert.Equal(0, first.Result.DuplicateSessions);
        Assert.Empty(first.Result.Rejected);

        // A new client instance proves idempotency is persisted by the transport rather than
        // being an in-memory property of one coordinator invocation.
        var secondCoordinator = new PlaytimeSyncCoordinator(new LocalFileSyncClient(SyncDirectory));
        var second = await secondCoordinator.SyncMeasuredSessionsAsync(
            persistedCutover,
            persistedSessions);

        Assert.Equal(0, second.Result.AcceptedSessions);
        Assert.Equal(1, second.Result.DuplicateSessions);
        Assert.Empty(second.Result.Rejected);

        var receiptPath = Path.Combine(SyncDirectory, "sync-receipts.jsonl");
        var receipts = await File.ReadAllTextAsync(receiptPath);
        Assert.Contains("\"tracking_started_at_utc\":", receipts, StringComparison.Ordinal);
        Assert.Contains($"\"game_id\":\"{game.Id:D}\"", receipts, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"started_at_utc\":", receipts, StringComparison.Ordinal);
        Assert.Contains("\"ended_at_utc\":", receipts, StringComparison.Ordinal);
        Assert.Contains("\"capture_method\":\"reconciliation\"", receipts, StringComparison.Ordinal);
        Assert.Contains("\"confidence\":\"high\"", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("catalogo_juego_id", receipts, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(game.Title, receipts, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReusedSessionIdWithDifferentData_IsRejectedAsIdempotencyConflict()
    {
        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        var sessionId = Guid.NewGuid();
        var gameId = Guid.NewGuid();
        var client = new LocalFileSyncClient(SyncDirectory);

        var original = new PlaytimeSyncBatch(
            cutover,
            new[]
            {
                new SessionSyncItem(
                    sessionId,
                    gameId,
                    cutover.AddMinutes(1),
                    cutover.AddMinutes(2),
                    "wmi",
                    "exact")
            },
            Array.Empty<HistoricalEvidenceSyncItem>());

        var changed = original with
        {
            Sessions = new[]
            {
                original.Sessions[0] with { EndedAtUtc = cutover.AddMinutes(3) }
            }
        };

        var first = await client.SyncPlaytimeAsync(original);
        var second = await client.SyncPlaytimeAsync(changed);

        Assert.Equal(1, first.AcceptedSessions);
        var rejection = Assert.Single(second.Rejected);
        Assert.Equal("idempotency_conflict", rejection.Code);
        Assert.Equal(sessionId, rejection.ClientId);
    }

    [Fact]
    public void SessionBeforeTrackingCutover_IsRejectedBeforeTransport()
    {
        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        var session = new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            cutover.AddMinutes(-1),
            cutover.AddMinutes(1),
            CaptureMethod.InitialSnapshot,
            Confidence.High);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlaytimeSyncBatchBuilder.BuildMeasuredSessions(
                cutover,
                new[] { session }));

        Assert.Contains("before the tracking cutover", exception.Message, StringComparison.Ordinal);
    }
}
