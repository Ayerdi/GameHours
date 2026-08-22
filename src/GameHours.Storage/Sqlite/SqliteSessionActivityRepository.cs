using GameHours.Core.Abstractions;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteSessionActivityRepository : ISessionActivityRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteSessionActivityRepository(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task UpsertAsync(
        SessionActivityMetrics metrics,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        if (metrics.SessionId == Guid.Empty) throw new ArgumentException("Session id cannot be empty.", nameof(metrics));
        if (metrics.GameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(metrics));
        if (metrics.FocusedDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(metrics));
        if (metrics.ActiveDuration < TimeSpan.Zero || metrics.ActiveDuration > metrics.FocusedDuration)
            throw new ArgumentOutOfRangeException(nameof(metrics), "Active duration must be between zero and focused duration.");
        if (metrics.IdleThreshold < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(metrics));
        if (metrics.AfkFilterEnabled != (metrics.IdleThreshold > TimeSpan.Zero))
            throw new ArgumentException("AFK filter state must match whether the idle threshold is enabled.", nameof(metrics));

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO session_activity(
                session_id,
                game_id,
                focused_duration_ms,
                active_duration_ms,
                idle_threshold_ms,
                is_finalized,
                updated_at_utc,
                afk_filter_enabled)
            VALUES(
                $session_id,
                $game_id,
                $focused_duration_ms,
                $active_duration_ms,
                $idle_threshold_ms,
                $is_finalized,
                $updated_at_utc,
                $afk_filter_enabled)
            ON CONFLICT(session_id) DO UPDATE SET
                game_id = excluded.game_id,
                focused_duration_ms = excluded.focused_duration_ms,
                active_duration_ms = excluded.active_duration_ms,
                idle_threshold_ms = excluded.idle_threshold_ms,
                is_finalized = excluded.is_finalized,
                updated_at_utc = excluded.updated_at_utc,
                afk_filter_enabled = excluded.afk_filter_enabled;
            """;
        command.Parameters.AddWithValue("$session_id", metrics.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$game_id", metrics.GameId.ToString("D"));
        command.Parameters.AddWithValue("$focused_duration_ms", checked((long)metrics.FocusedDuration.TotalMilliseconds));
        command.Parameters.AddWithValue("$active_duration_ms", checked((long)metrics.ActiveDuration.TotalMilliseconds));
        command.Parameters.AddWithValue("$idle_threshold_ms", checked((long)metrics.IdleThreshold.TotalMilliseconds));
        command.Parameters.AddWithValue("$is_finalized", metrics.IsFinalized ? 1 : 0);
        command.Parameters.AddWithValue("$updated_at_utc", SqliteTime.Serialize(metrics.UpdatedAtUtc));
        command.Parameters.AddWithValue("$afk_filter_enabled", metrics.AfkFilterEnabled ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<SessionActivityMetrics?> GetBySessionIdAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE session_id = $session_id;
            """;
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<SessionActivityMetrics>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            WHERE game_id = $game_id
            ORDER BY updated_at_utc;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<SessionActivityMetrics>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            {SelectColumns}
            ORDER BY updated_at_utc;
            """;
        return await ReadAllAsync(command, cancellationToken);
    }

    public async Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM session_activity WHERE session_id = $session_id;";
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<SessionActivityMetrics>> ReadAllAsync(
        SqliteCommand command,
        CancellationToken cancellationToken)
    {
        var rows = new List<SessionActivityMetrics>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) rows.Add(Read(reader));
        return rows;
    }

    private static SessionActivityMetrics Read(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            TimeSpan.FromMilliseconds(reader.GetInt64(2)),
            TimeSpan.FromMilliseconds(reader.GetInt64(3)),
            TimeSpan.FromMilliseconds(reader.GetInt64(4)),
            reader.GetInt64(7) != 0,
            reader.GetInt64(5) != 0,
            SqliteTime.Deserialize(reader.GetString(6)));

    private const string SelectColumns = """
        SELECT session_id,
               game_id,
               focused_duration_ms,
               active_duration_ms,
               idle_threshold_ms,
               is_finalized,
               updated_at_utc,
               afk_filter_enabled
        FROM session_activity
        """;
}
