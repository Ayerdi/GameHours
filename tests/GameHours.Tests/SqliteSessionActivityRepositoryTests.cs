using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteSessionActivityRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-session-activity",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Upsert_RoundTripsFocusedAndActiveDurations()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Attention Test");
        var otherGame = new TrackedGame(Guid.NewGuid(), "Other Attention Test");
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(game);
        await games.UpsertAsync(otherGame);
        var repository = new SqliteSessionActivityRepository(database);
        var openSessions = new SqliteOpenSessionRepository(database);
        var sessionId = Guid.NewGuid();
        var otherSessionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 22, 19, 0, 0, TimeSpan.Zero);

        await openSessions.UpsertAsync(new OpenSessionCheckpoint(
            sessionId, game.Id, now, now, CaptureMethod.Wmi));
        await openSessions.UpsertAsync(new OpenSessionCheckpoint(
            otherSessionId, otherGame.Id, now, now, CaptureMethod.Wmi));

        await repository.UpsertAsync(new SessionActivityMetrics(
            sessionId,
            game.Id,
            TimeSpan.FromMinutes(20),
            TimeSpan.FromMinutes(15),
            TimeSpan.FromMinutes(5),
            false,
            now));

        await repository.UpsertAsync(new SessionActivityMetrics(
            sessionId,
            game.Id,
            TimeSpan.FromMinutes(30),
            TimeSpan.FromMinutes(22),
            TimeSpan.FromMinutes(5),
            true,
            now.AddMinutes(10)));

        await repository.UpsertAsync(new SessionActivityMetrics(
            otherSessionId,
            otherGame.Id,
            TimeSpan.FromMinutes(4),
            TimeSpan.FromMinutes(3),
            TimeSpan.FromMinutes(5),
            true,
            now));

        var stored = Assert.IsType<SessionActivityMetrics>(
            await repository.GetBySessionIdAsync(sessionId));
        Assert.Equal(TimeSpan.FromMinutes(30), stored.FocusedDuration);
        Assert.Equal(TimeSpan.FromMinutes(22), stored.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), stored.IdleThreshold);
        Assert.True(stored.AfkFilterEnabled);
        Assert.True(stored.IsFinalized);
        Assert.Equal(now.AddMinutes(10), stored.UpdatedAtUtc);

        var forGame = Assert.Single(await repository.GetForGameAsync(game.Id));
        Assert.Equal(sessionId, forGame.SessionId);
        Assert.Equal(2, (await repository.GetAllAsync()).Count);

        await repository.DeleteAsync(sessionId);
        Assert.Null(await repository.GetBySessionIdAsync(sessionId));
    }

    [Fact]
    public async Task Upsert_RoundTripsDisabledAfkPolicyWithoutFakeActiveEstimate()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Focus Only Test");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteSessionActivityRepository(database);
        var sessionId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await new SqliteOpenSessionRepository(database).UpsertAsync(new OpenSessionCheckpoint(
            sessionId, game.Id, now, now, CaptureMethod.Wmi));

        await repository.UpsertAsync(new SessionActivityMetrics(
            sessionId,
            game.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.Zero,
            TimeSpan.Zero,
            true,
            now));

        var stored = Assert.IsType<SessionActivityMetrics>(
            await repository.GetBySessionIdAsync(sessionId));
        Assert.Equal(TimeSpan.Zero, stored.IdleThreshold);
        Assert.False(stored.AfkFilterEnabled);
        Assert.Equal(TimeSpan.FromMinutes(8), stored.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, stored.ActiveDuration);
    }

    [Fact]
    public async Task Upsert_RejectsActiveDurationWhenAfkEstimationIsDisabled()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Invalid Focus Only Test");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteSessionActivityRepository(database);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                Guid.NewGuid(),
                game.Id,
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(8),
                TimeSpan.Zero,
                true,
                DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Upsert_RejectsActiveDurationGreaterThanFocusedDuration()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Invalid Attention Test");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteSessionActivityRepository(database);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                Guid.NewGuid(),
                game.Id,
                TimeSpan.FromMinutes(1),
                TimeSpan.FromMinutes(2),
                TimeSpan.FromMinutes(5),
                true,
                DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Upsert_AcceptsSessionFinalizedInSessionsAsAuthoritative()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Finalized Authoritative Test");
        await new SqliteGameRepository(database).UpsertAsync(game);

        var start = new DateTimeOffset(2026, 8, 22, 20, 0, 0, TimeSpan.Zero);
        var session = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            start,
            start.AddMinutes(10),
            CaptureMethod.Wmi,
            Confidence.High,
            "ReconciledStop");
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));

        var repository = new SqliteSessionActivityRepository(database);
        await repository.UpsertAsync(new SessionActivityMetrics(
            session.Id,
            game.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(6),
            TimeSpan.FromMinutes(5),
            true,
            session.EndedAtUtc));

        var stored = Assert.IsType<SessionActivityMetrics>(
            await repository.GetBySessionIdAsync(session.Id));
        Assert.Equal(session.Id, stored.SessionId);
        Assert.Equal(game.Id, stored.GameId);
        Assert.Equal(TimeSpan.FromMinutes(8), stored.FocusedDuration);
    }

    [Fact]
    public async Task Upsert_RejectsSessionIdWithNoAuthoritativeIdentity()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "No Identity Test");
        await new SqliteGameRepository(database).UpsertAsync(game);

        var repository = new SqliteSessionActivityRepository(database);
        var orphanSessionId = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                orphanSessionId,
                game.Id,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(5),
                true,
                DateTimeOffset.UtcNow)));

        Assert.Null(await repository.GetBySessionIdAsync(orphanSessionId));
    }

    [Fact]
    public async Task Upsert_RejectsWrongGameForFinalizedSession()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var sessionGame = new TrackedGame(Guid.NewGuid(), "Finalized Owner A");
        var otherGame = new TrackedGame(Guid.NewGuid(), "Other Game B");
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(sessionGame);
        await games.UpsertAsync(otherGame);

        var start = new DateTimeOffset(2026, 8, 22, 21, 0, 0, TimeSpan.Zero);
        var session = new PlaySession(
            Guid.NewGuid(),
            sessionGame.Id,
            start,
            start.AddMinutes(10),
            CaptureMethod.Wmi,
            Confidence.High,
            "ReconciledStop");
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));

        var repository = new SqliteSessionActivityRepository(database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                session.Id,
                otherGame.Id,
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(6),
                TimeSpan.FromMinutes(5),
                true,
                session.EndedAtUtc)));

        Assert.Null(await repository.GetBySessionIdAsync(session.Id));
    }

    [Fact]
    public async Task Upsert_RejectsWrongGameForActiveSession()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var activeOwner = new TrackedGame(Guid.NewGuid(), "Active Owner A");
        var otherGame = new TrackedGame(Guid.NewGuid(), "Other Game B");
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(activeOwner);
        await games.UpsertAsync(otherGame);

        var now = DateTimeOffset.UtcNow;
        var activeSessionId = Guid.NewGuid();
        await new SqliteOpenSessionRepository(database).UpsertAsync(
            new OpenSessionCheckpoint(activeSessionId, activeOwner.Id, now, now, CaptureMethod.Wmi));

        var repository = new SqliteSessionActivityRepository(database);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                activeSessionId,
                otherGame.Id,
                TimeSpan.FromMinutes(5),
                TimeSpan.FromMinutes(4),
                TimeSpan.FromMinutes(5),
                false,
                now)));

        Assert.Null(await repository.GetBySessionIdAsync(activeSessionId));
    }

    [Fact]
    public async Task Upsert_ConflictUpdateCannotSilentlyChangeGameToAnother()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();

        var gameA = new TrackedGame(Guid.NewGuid(), "Original A");
        var gameB = new TrackedGame(Guid.NewGuid(), "Intruder B");
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(gameA);
        await games.UpsertAsync(gameB);

        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        await new SqliteOpenSessionRepository(database).UpsertAsync(
            new OpenSessionCheckpoint(sessionId, gameA.Id, now, now, CaptureMethod.Wmi));

        var repository = new SqliteSessionActivityRepository(database);
        await repository.UpsertAsync(new SessionActivityMetrics(
            sessionId,
            gameA.Id,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(5),
            false,
            now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.UpsertAsync(
            new SessionActivityMetrics(
                sessionId,
                gameB.Id,
                TimeSpan.FromMinutes(10),
                TimeSpan.FromMinutes(8),
                TimeSpan.FromMinutes(5),
                true,
                now)));

        var stored = Assert.IsType<SessionActivityMetrics>(
            await repository.GetBySessionIdAsync(sessionId));
        Assert.Equal(gameA.Id, stored.GameId);
        Assert.Equal(TimeSpan.FromMinutes(10), stored.FocusedDuration);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
