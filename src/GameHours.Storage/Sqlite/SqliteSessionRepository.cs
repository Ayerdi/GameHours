using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteSessionRepository : ISessionRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteSessionRepository(GameHoursDatabase database)
    {
        _database = database;
    }

    public async Task<bool> AddAsync(
        PlaySession session,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(
                id, game_id, started_at_utc, ended_at_utc,
                duration_ms, capture_method, confidence, end_reason, created_at_utc)
            VALUES(
                $id, $gameId, $startedAtUtc, $endedAtUtc,
                $durationMs, $captureMethod, $confidence, $endReason, $createdAtUtc)
            ON CONFLICT(id) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$id", session.Id.ToString("D"));
        command.Parameters.AddWithValue("$gameId", session.GameId.ToString("D"));
        command.Parameters.AddWithValue("$startedAtUtc", SqliteTime.Serialize(session.StartedAtUtc));
        command.Parameters.AddWithValue("$endedAtUtc", SqliteTime.Serialize(session.EndedAtUtc));
        command.Parameters.AddWithValue("$durationMs", checked((long)Math.Round(session.Duration.TotalMilliseconds)));
        command.Parameters.AddWithValue("$captureMethod", (int)session.CaptureMethod);
        command.Parameters.AddWithValue("$confidence", (int)session.Confidence);
        command.Parameters.AddWithValue("$endReason", (object?)session.EndReason ?? DBNull.Value);
        command.Parameters.AddWithValue("$createdAtUtc", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<PlaySession>> GetForGameAsync(
        Guid gameId,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        CancellationToken cancellationToken = default)
    {
        var results = new List<PlaySession>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();

        var clauses = new List<string> { "game_id = $gameId" };
        command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));

        if (fromUtc is not null)
        {
            clauses.Add("ended_at_utc > $fromUtc");
            command.Parameters.AddWithValue("$fromUtc", SqliteTime.Serialize(fromUtc.Value));
        }

        if (toUtc is not null)
        {
            clauses.Add("started_at_utc < $toUtc");
            command.Parameters.AddWithValue("$toUtc", SqliteTime.Serialize(toUtc.Value));
        }

        command.CommandText = $"""
            SELECT id, game_id, started_at_utc, ended_at_utc,
                   capture_method, confidence, end_reason
            FROM sessions
            WHERE {string.Join(" AND ", clauses)}
            ORDER BY started_at_utc;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadSession(reader));
        }

        return results;
    }

    public async Task<bool> HasOverlapAsync(
        Guid gameId,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM sessions
                WHERE game_id = $gameId
                  AND started_at_utc < $periodEndUtc
                  AND ended_at_utc > $periodStartUtc
            );
            """;
        command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));
        command.Parameters.AddWithValue("$periodStartUtc", SqliteTime.Serialize(periodStartUtc));
        command.Parameters.AddWithValue("$periodEndUtc", SqliteTime.Serialize(periodEndUtc));
        var value = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
        return value == 1;
    }

    private static PlaySession ReadSession(SqliteDataReader reader)
    {
        return new PlaySession(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            SqliteTime.Deserialize(reader.GetString(2)),
            SqliteTime.Deserialize(reader.GetString(3)),
            (CaptureMethod)reader.GetInt32(4),
            (Confidence)reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetString(6));
    }
}
