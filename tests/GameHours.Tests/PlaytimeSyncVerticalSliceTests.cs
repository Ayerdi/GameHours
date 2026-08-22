using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using GameHours.Sync;

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
    public async Task PersistedMeasuredSession_TravelsThroughSyncBoundary_Idempotently()
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
        var catalogMappings = new Dictionary<Guid, long> { [game.Id] = 381L };

        var firstCoordinator = new PlaytimeSyncCoordinator(new LocalFileSyncClient(SyncDirectory));
        var first = await firstCoordinator.SyncMeasuredSessionsAsync(
            persistedCutover.Value,
            persistedSessions,
            catalogMappings);

        Assert.Equal(1, first.SentSessions);
        Assert.Empty(first.UnmappedGameIds);
        Assert.Equal(1, first.Result.AcceptedSessions);
        Assert.Equal(0, first.Result.DuplicateSessions);
        Assert.Empty(first.Result.Rejected);

        // A new client instance proves idempotency is persisted by the transport rather than
        // being an in-memory property of one coordinator invocation.
        var secondCoordinator = new PlaytimeSyncCoordinator(new LocalFileSyncClient(SyncDirectory));
        var second = await secondCoordinator.SyncMeasuredSessionsAsync(
            persistedCutover.Value,
            persistedSessions,
            catalogMappings);

        Assert.Equal(0, second.Result.AcceptedSessions);
        Assert.Equal(1, second.Result.DuplicateSessions);
        Assert.Empty(second.Result.Rejected);

        var receiptPath = Path.Combine(SyncDirectory, "sync-receipts.jsonl");
        var receipts = await File.ReadAllTextAsync(receiptPath);
        Assert.Contains("\"tracking_started_at\":", receipts, StringComparison.Ordinal);
        Assert.Contains("\"catalogo_juego_id\":381", receipts, StringComparison.Ordinal);
        Assert.Contains("\"started_at\":", receipts, StringComparison.Ordinal);
        Assert.Contains("\"ended_at\":", receipts, StringComparison.Ordinal);
        Assert.Contains("\"capture_method\":\"reconciliation\"", receipts, StringComparison.Ordinal);
        Assert.Contains("\"confidence\":\"high\"", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("tracking_started_at_utc", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("started_at_utc", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain("ended_at_utc", receipts, StringComparison.Ordinal);
        Assert.DoesNotContain(game.Title, receipts, StringComparison.Ordinal);
        Assert.DoesNotContain(game.Id.ToString("D"), receipts, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnmappedGame_IsReportedAndNotSent()
    {
        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        var gameId = Guid.NewGuid();
        var session = new PlaySession(
            Guid.NewGuid(),
            gameId,
            cutover.AddMinutes(1),
            cutover.AddMinutes(2),
            CaptureMethod.Wmi,
            Confidence.Exact);

        var coordinator = new PlaytimeSyncCoordinator(new LocalFileSyncClient(SyncDirectory));
        var execution = await coordinator.SyncMeasuredSessionsAsync(
            cutover,
            new[] { session },
            new Dictionary<Guid, long>());

        Assert.Equal(0, execution.SentSessions);
        Assert.Equal(new[] { gameId }, execution.UnmappedGameIds);
        Assert.Equal(0, execution.Result.AcceptedSessions);
        Assert.Equal(0, execution.Result.DuplicateSessions);
        Assert.Empty(execution.Result.Rejected);
    }

    [Fact]
    public void SessionBeforeTrackingCutover_IsRejectedBeforeTransport()
    {
        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        var gameId = Guid.NewGuid();
        var session = new PlaySession(
            Guid.NewGuid(),
            gameId,
            cutover.AddMinutes(-1),
            cutover.AddMinutes(1),
            CaptureMethod.InitialSnapshot,
            Confidence.High);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            PlaytimeSyncBatchBuilder.BuildMeasuredSessions(
                cutover,
                new[] { session },
                new Dictionary<Guid, long> { [gameId] = 381L }));

        Assert.Contains("before the tracking cutover", exception.Message, StringComparison.Ordinal);
    }
}
