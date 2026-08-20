using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteRepositoriesTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-tests",
        Guid.NewGuid().ToString("N"));

    private GameHoursDatabase Database =>
        new(Path.Combine(_directory, "gamehours.db"));

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
    public async Task SessionInsert_IsIdempotentBySessionId()
    {
        var database = Database;
        await database.InitializeAsync();
        var repository = new SqliteSessionRepository(database);
        var session = new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-20T18:10:21Z"),
            DateTimeOffset.Parse("2026-08-20T18:11:27Z"),
            CaptureMethod.Reconciliation,
            Confidence.High);

        Assert.True(await repository.AddAsync(session));
        Assert.False(await repository.AddAsync(session));

        var rows = await repository.GetForGameAsync(session.GameId);
        Assert.Single(rows);
    }

    [Fact]
    public async Task GapRecovery_IsRejectedWhenItOverlapsMeasuredSession()
    {
        var database = Database;
        await database.InitializeAsync();
        var tracking = new SqliteTrackingStateRepository(database);
        var sessions = new SqliteSessionRepository(database);
        var evidenceRepository = new SqliteHistoricalEvidenceRepository(
            database,
            tracking,
            sessions);

        var cutover = DateTimeOffset.Parse("2026-08-20T18:00:00Z");
        await tracking.GetOrSetTrackingStartedAtAsync(cutover);

        var gameId = Guid.NewGuid();
        await sessions.AddAsync(new PlaySession(
            Guid.NewGuid(),
            gameId,
            cutover.AddHours(1),
            cutover.AddHours(2),
            CaptureMethod.Reconciliation,
            Confidence.High));

        var gap = new HistoricalEvidence(
            Guid.NewGuid(),
            gameId,
            HistoricalSource.Srum,
            EvidenceKind.GapRecovery,
            PlaytimeMetric.Foreground,
            Confidence.Estimated,
            cutover.AddMinutes(30),
            cutover.AddHours(3),
            TimeSpan.FromHours(1));

        await Assert.ThrowsAsync<TimelineConflictException>(() =>
            evidenceRepository.AddAsync(gap));
    }

    [Fact]
    public async Task TrackingCutover_IsSetOnce()
    {
        var database = Database;
        await database.InitializeAsync();
        var tracking = new SqliteTrackingStateRepository(database);
        var first = DateTimeOffset.Parse("2026-08-20T18:00:00Z");
        var later = first.AddDays(1);

        var storedFirst = await tracking.GetOrSetTrackingStartedAtAsync(first);
        var storedSecond = await tracking.GetOrSetTrackingStartedAtAsync(later);

        Assert.Equal(first, storedFirst);
        Assert.Equal(first, storedSecond);
    }
}
