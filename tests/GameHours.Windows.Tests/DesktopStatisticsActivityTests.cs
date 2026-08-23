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
            TimeSpan.Zero,
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

    [Fact]
    public void GameRow_FocusOnlyTelemetry_DoesNotMasqueradeAsEstimatedActiveTime()
    {
        var gameId = Guid.NewGuid();
        var recentSession = new DesktopActivityRow(
            Guid.NewGuid(),
            gameId,
            "Focus only",
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(7),
            ActiveDuration: null,
            EndReason: "Test");
        var row = new DesktopGameRow(
            gameId,
            "Focus only",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(7),
            ActivePlaytime: null,
            ActivityMeasuredSessionCount: 1,
            FirstActivityAtUtc: recentSession.StartedAtUtc,
            LastActivityAtUtc: recentSession.EndedAtUtc,
            FirstMeasuredSessionAtUtc: recentSession.StartedAtUtc,
            LastMeasuredSessionAtUtc: recentSession.EndedAtUtc,
            MeasuredSessionCount: 1,
            ExecutablePath: null,
            RecentSessions: [recentSession]);

        var viewModel = new MainWindow.GameRowViewModel(row);

        Assert.Equal("—", viewModel.ActiveText);
        Assert.Contains("filtro AFK", viewModel.ActivityCoverageText);
        var recent = Assert.Single(viewModel.RecentSessions);
        Assert.Contains("foco", recent.ReasonText);
        Assert.Contains("AFK no estimado", recent.ReasonText);
    }

    [Fact]
    public void GameRow_ZeroEstimatedActiveTime_RemainsARealObservedZero()
    {
        var gameId = Guid.NewGuid();
        var row = new DesktopGameRow(
            gameId,
            "Observed zero",
            TimeSpan.FromMinutes(10),
            TimeSpan.FromMinutes(10),
            TimeSpan.Zero,
            TimeSpan.FromMinutes(8),
            ActivePlaytime: TimeSpan.Zero,
            ActivityMeasuredSessionCount: 1,
            FirstActivityAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            LastActivityAtUtc: DateTimeOffset.UtcNow,
            FirstMeasuredSessionAtUtc: DateTimeOffset.UtcNow.AddMinutes(-10),
            LastMeasuredSessionAtUtc: DateTimeOffset.UtcNow,
            MeasuredSessionCount: 1,
            ExecutablePath: null,
            RecentSessions: []);

        var viewModel = new MainWindow.GameRowViewModel(row);

        Assert.Equal("0 min", viewModel.ActiveText);
    }

    [Fact]
    public async Task ReadModels_IgnoreFinalizedActivityThatDoesNotMatchItsAuthoritativeSession()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "mismatched-activity.db");
        var database = new GameHoursDatabase(path);
        await database.InitializeAsync();

        var sessionGame = new TrackedGame(Guid.NewGuid(), "Session owner");
        var unrelatedGame = new TrackedGame(Guid.NewGuid(), "Metric owner");
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(sessionGame);
        await games.UpsertAsync(unrelatedGame);

        var start = new DateTimeOffset(2026, 8, 10, 20, 0, 0, TimeSpan.Zero);
        var session = new PlaySession(
            Guid.NewGuid(),
            sessionGame.Id,
            start,
            start.AddMinutes(10),
            CaptureMethod.Wmi,
            Confidence.High,
            "Test");
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));
        await new SqliteSessionActivityRepository(database).UpsertAsync(new SessionActivityMetrics(
            session.Id,
            unrelatedGame.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(7),
            TimeSpan.FromMinutes(5),
            true,
            session.EndedAtUtc));

        var statistics = await new DesktopStatisticsService(path, TimeZoneInfo.Utc)
            .LoadAsync(new DateOnly(2026, 8, 1));
        var unrelatedInsight = await new DesktopGameInsightService(path).LoadAsync(unrelatedGame.Id);

        Assert.Equal(TimeSpan.FromMinutes(10), statistics.Lifetime.MeasuredPlaytime);
        Assert.Equal(TimeSpan.Zero, statistics.Lifetime.ActivityCoveredPlaytime);
        Assert.Equal(0, statistics.Lifetime.ActivityMeasuredSessionCount);
        Assert.Null(unrelatedInsight.FocusedPlaytime);
        Assert.Equal(0, unrelatedInsight.ActivitySessionCount);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void SessionClock_RunsOnlyForVisibleWindowWithActiveSession(
        bool hasActiveSession,
        bool isVisible,
        bool expected)
    {
        var startedAt = hasActiveSession ? DateTimeOffset.UtcNow : (DateTimeOffset?)null;

        Assert.Equal(expected, MainWindow.ShouldRunSessionClock(startedAt, isVisible));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
