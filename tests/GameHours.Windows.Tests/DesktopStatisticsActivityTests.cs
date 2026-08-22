using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Windows.Tests;

public sealed class DesktopStatisticsActivityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-statistics-activity",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LifetimeAttention_DoesNotPresentFocusOnlySessionsAsAfkEstimated()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "gamehours.db");
        var database = new GameHoursDatabase(path);
        await database.InitializeAsync();

        var game = new TrackedGame(Guid.NewGuid(), "Attention provenance");
        var games = new SqliteGameRepository(database);
        var sessions = new SqliteSessionRepository(database);
        var activity = new SqliteSessionActivityRepository(database);
        await games.UpsertAsync(game);

        var start = new DateTimeOffset(2026, 8, 10, 18, 0, 0, TimeSpan.Zero);
        var withAfk = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            start,
            start.AddMinutes(10),
            CaptureMethod.Wmi,
            Confidence.High,
            "Test");
        var focusOnly = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            start.AddHours(1),
            start.AddHours(1).AddMinutes(10),
            CaptureMethod.Wmi,
            Confidence.High,
            "Test");

        Assert.True(await sessions.AddAsync(withAfk));
        Assert.True(await sessions.AddAsync(focusOnly));
        await activity.UpsertAsync(new SessionActivityMetrics(
            withAfk.Id,
            game.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(6),
            TimeSpan.FromMinutes(5),
            true,
            start.AddMinutes(10)));
        await activity.UpsertAsync(new SessionActivityMetrics(
            focusOnly.Id,
            game.Id,
            TimeSpan.FromMinutes(7),
            TimeSpan.FromMinutes(7),
            TimeSpan.Zero,
            true,
            start.AddHours(1).AddMinutes(10)));

        var snapshot = await new DesktopStatisticsService(path, TimeZoneInfo.Utc)
            .LoadAsync(new DateOnly(2026, 8, 1));

        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.Lifetime.MeasuredPlaytime);
        Assert.Equal(TimeSpan.FromMinutes(20), snapshot.Lifetime.ActivityCoveredPlaytime);
        Assert.Equal(TimeSpan.FromMinutes(15), snapshot.Lifetime.FocusedPlaytime);
        Assert.Equal(TimeSpan.FromMinutes(10), snapshot.Lifetime.AfkEstimatedCoveredPlaytime);
        Assert.Equal(TimeSpan.FromMinutes(6), snapshot.Lifetime.EstimatedActivePlaytime);
        Assert.Equal(2, snapshot.Lifetime.ActivityMeasuredSessionCount);
        Assert.Equal(1, snapshot.Lifetime.AfkEstimatedSessionCount);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
