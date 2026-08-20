using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteOpenSessionRepository : IOpenSessionRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteOpenSessionRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(
        OpenSessionCheckpoint checkpoint,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO open_sessions(
                session_id, game_id, started_at_utc, last_checkpoint_at_utc,
                capture_method, created_at_utc, updated_at_utc)
            VALUES(
                $sessionId, $gameId, $startedAtUtc, $lastCheckpointAtUtc,
                $captureMethod, $now, $now)
            ON CONFLICT(session_id) DO UPDATE SET
                game_id = excluded.game_id,
                started_at_utc = excluded.started_at_utc,
                last_checkpoint_at_utc = excluded.last_checkpoint_at_utc,
                capture_method = excluded.capture_method,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$sessionId", checkpoint.SessionId.ToString("D"));
        command.Parameters.AddWithValue("$gameId", checkpoint.GameId.ToString("D"));
        command.Parameters.AddWithValue("$startedAtUtc", SqliteTime.Serialize(checkpoint.StartedAtUtc));
        command.Parameters.AddWithValue("$lastCheckpointAtUtc", SqliteTime.Serialize(checkpoint.LastCheckpointAtUtc));
        command.Parameters.AddWithValue("$captureMethod", (int)checkpoint.CaptureMethod);
        command.Parameters.AddWithValue("$now", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<OpenSessionCheckpoint>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<OpenSessionCheckpoint>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, game_id, started_at_utc, last_checkpoint_at_utc, capture_method
            FROM open_sessions
            ORDER BY started_at_utc;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new OpenSessionCheckpoint(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                SqliteTime.Deserialize(reader.GetString(2)),
                SqliteTime.Deserialize(reader.GetString(3)),
                (CaptureMethod)reader.GetInt32(4)));
        }

        return results;
    }

    public async Task DeleteAsync(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM open_sessions WHERE session_id = $sessionId;";
        command.Parameters.AddWithValue("$sessionId", sessionId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
