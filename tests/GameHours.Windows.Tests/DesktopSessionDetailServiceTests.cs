using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Windows.Tests;

public sealed class DesktopSessionDetailServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-session-detail",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Load_AfkEstimatedSession_ReportsFocusActiveAndAfkWithoutInventingUnknownTime()
    {
        var path = await CreateDatabaseAsync();
        var database = new GameHoursDatabase(path);
        var game = new TrackedGame(Guid.NewGuid(), "Detail test");
        var session = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 23, 10, 10, 0, TimeSpan.Zero),
            CaptureMethod.Wmi,
            Confidence.High,
            "ReconciledStop");

        await new SqliteGameRepository(database).UpsertAsync(game);
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));
        await new SqliteSessionActivityRepository(database).UpsertAsync(new SessionActivityMetrics(
            session.Id,
            game.Id,
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(2),
            true,
            session.EndedAtUtc));

        var detail = await new DesktopSessionDetailService(path).LoadAsync(session.Id);

        Assert.NotNull(detail);
        Assert.Equal(TimeSpan.FromMinutes(8), detail.FocusedDuration);
        Assert.Equal(TimeSpan.FromMinutes(5), detail.ActiveDuration);
        Assert.Equal(TimeSpan.FromMinutes(3), detail.AfkDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), detail.UnfocusedOrUnknownDuration);
        Assert.Equal(TimeSpan.FromMinutes(2), detail.IdleThreshold);
        Assert.True(detail.AfkFilterEnabled);
    }

    [Fact]
    public async Task Load_FocusOnlySession_LeavesActiveAndAfkUnavailable()
    {
        var path = await CreateDatabaseAsync();
        var database = new GameHoursDatabase(path);
        var game = new TrackedGame(Guid.NewGuid(), "Focus only detail");
        var session = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            new DateTimeOffset(2026, 8, 23, 11, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 23, 11, 10, 0, TimeSpan.Zero),
            CaptureMethod.Wmi,
            Confidence.High,
            "ReconciledStop");

        await new SqliteGameRepository(database).UpsertAsync(game);
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));
        await new SqliteSessionActivityRepository(database).UpsertAsync(new SessionActivityMetrics(
            session.Id,
            game.Id,
            TimeSpan.FromMinutes(7),
            TimeSpan.Zero,
            TimeSpan.Zero,
            true,
            session.EndedAtUtc));

        var detail = await new DesktopSessionDetailService(path).LoadAsync(session.Id);

        Assert.NotNull(detail);
        Assert.Equal(TimeSpan.FromMinutes(7), detail.FocusedDuration);
        Assert.Null(detail.ActiveDuration);
        Assert.Null(detail.AfkDuration);
        Assert.Null(detail.IdleThreshold);
        Assert.Equal(TimeSpan.FromMinutes(3), detail.UnfocusedOrUnknownDuration);
        Assert.False(detail.AfkFilterEnabled);
    }

    private async Task<string> CreateDatabaseAsync()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.db");
        await new GameHoursDatabase(path).InitializeAsync();
        return path;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
