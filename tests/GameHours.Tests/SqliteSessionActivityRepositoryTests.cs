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
        var sessionId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 8, 22, 19, 0, 0, TimeSpan.Zero);

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
            Guid.NewGuid(),
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

        await repository.UpsertAsync(new SessionActivityMetrics(
            sessionId,
            game.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.Zero,
            TimeSpan.Zero,
            true,
            DateTimeOffset.UtcNow));

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

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
