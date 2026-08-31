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
        Assert.Equal(session.Id, detail.SessionId);
        Assert.Equal(game.Title, detail.GameTitle);
        Assert.Equal(session.StartedAtUtc, detail.StartedAtUtc);
        Assert.Equal(session.EndedAtUtc, detail.EndedAtUtc);
        Assert.Equal(session.Duration, detail.Duration);
        Assert.Equal(session.CaptureMethod, detail.CaptureMethod);
        Assert.Equal(session.Confidence, detail.Confidence);
        Assert.Equal(session.EndReason, detail.EndReason);
        Assert.True(detail.HasActivityTelemetry);
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

    [Fact]
    public async Task Load_SessionWithoutActivityTelemetry_LeavesAttentionMetricsUnavailable()
    {
        var path = await CreateDatabaseAsync();
        var database = new GameHoursDatabase(path);
        var game = new TrackedGame(Guid.NewGuid(), "Legacy detail");
        var session = new PlaySession(
            Guid.NewGuid(),
            game.Id,
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 23, 12, 10, 0, TimeSpan.Zero),
            CaptureMethod.Reconciliation,
            Confidence.High,
            "RecoveredFromCheckpoint");

        await new SqliteGameRepository(database).UpsertAsync(game);
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));

        var detail = await new DesktopSessionDetailService(path).LoadAsync(session.Id);

        Assert.NotNull(detail);
        Assert.False(detail.HasActivityTelemetry);
        Assert.Null(detail.FocusedDuration);
        Assert.Null(detail.ActiveDuration);
        Assert.Null(detail.AfkDuration);
        Assert.Null(detail.UnfocusedOrUnknownDuration);
        Assert.Null(detail.IdleThreshold);
        Assert.False(detail.AfkFilterEnabled);
    }

    [Fact]
    public async Task Load_UnknownSession_ReturnsNull()
    {
        var path = await CreateDatabaseAsync();

        var detail = await new DesktopSessionDetailService(path).LoadAsync(Guid.NewGuid());

        Assert.Null(detail);
    }

    [Fact]
    public async Task Load_MismatchedActivityGame_DoesNotAttributeTelemetryToSession()
    {
        var path = await CreateDatabaseAsync();
        var database = new GameHoursDatabase(path);
        var sessionGame = new TrackedGame(Guid.NewGuid(), "Authoritative game");
        var unrelatedGame = new TrackedGame(Guid.NewGuid(), "Unrelated game");
        var session = new PlaySession(
            Guid.NewGuid(),
            sessionGame.Id,
            new DateTimeOffset(2026, 8, 23, 13, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 23, 13, 10, 0, TimeSpan.Zero),
            CaptureMethod.Wmi,
            Confidence.High,
            "ReconciledStop");

        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(sessionGame);
        await games.UpsertAsync(unrelatedGame);
        Assert.True(await new SqliteSessionRepository(database).AddAsync(session));
        await new SqliteSessionActivityRepository(database).UpsertAsync(new SessionActivityMetrics(
            session.Id,
            sessionGame.Id,
            TimeSpan.FromMinutes(9),
            TimeSpan.FromMinutes(8),
            TimeSpan.FromMinutes(5),
            true,
            session.EndedAtUtc));

        // Start from telemetry that satisfies the write-time invariant, then corrupt only the
        // ownership field to simulate a historical database row and keep the read-side defense
        // under test without depending on Storage's private timestamp serialization.
        await using (var connection = database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE session_activity
                SET game_id = $wrong_game_id
                WHERE session_id = $session_id;
                """;
            command.Parameters.AddWithValue("$wrong_game_id", unrelatedGame.Id.ToString("D"));
            command.Parameters.AddWithValue("$session_id", session.Id.ToString("D"));
            Assert.Equal(1, await command.ExecuteNonQueryAsync());
        }

        var detail = await new DesktopSessionDetailService(path).LoadAsync(session.Id);

        Assert.NotNull(detail);
        Assert.Equal(sessionGame.Title, detail.GameTitle);
        Assert.False(detail.HasActivityTelemetry);
        Assert.Null(detail.FocusedDuration);
        Assert.Null(detail.ActiveDuration);
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
