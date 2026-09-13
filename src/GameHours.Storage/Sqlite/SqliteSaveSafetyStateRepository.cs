namespace GameHours.Storage.Sqlite;

public enum SaveSafetyOperationStatus
{
    Success = 1,
    Partial = 2,
    Failed = 3
}

public sealed record SaveSafetyState(
    Guid GameId,
    DateTimeOffset LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    SaveSafetyOperationStatus LastStatus,
    string? LastErrorCode,
    int? LastFileCount,
    long? LastTotalBytes,
    bool? LastChanged,
    DateTimeOffset UpdatedAtUtc);

public sealed class SqliteSaveSafetyStateRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteSaveSafetyStateRepository(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<SaveSafetyState?> GetAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id,
                   last_attempt_at_utc,
                   last_success_at_utc,
                   last_status,
                   last_error_code,
                   last_file_count,
                   last_total_bytes,
                   last_changed,
                   updated_at_utc
            FROM save_safety_state
            WHERE game_id = $game_id;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpsertAsync(
        SaveSafetyState state,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(state);
        Validate(state);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO save_safety_state(
                game_id,
                last_attempt_at_utc,
                last_success_at_utc,
                last_status,
                last_error_code,
                last_file_count,
                last_total_bytes,
                last_changed,
                updated_at_utc)
            SELECT
                $game_id,
                $last_attempt_at_utc,
                $last_success_at_utc,
                $last_status,
                $last_error_code,
                $last_file_count,
                $last_total_bytes,
                $last_changed,
                $updated_at_utc
            WHERE EXISTS (SELECT 1 FROM games WHERE id = $game_id)
            ON CONFLICT(game_id) DO UPDATE SET
                last_attempt_at_utc = excluded.last_attempt_at_utc,
                last_success_at_utc = excluded.last_success_at_utc,
                last_status = excluded.last_status,
                last_error_code = excluded.last_error_code,
                last_file_count = excluded.last_file_count,
                last_total_bytes = excluded.last_total_bytes,
                last_changed = excluded.last_changed,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$game_id", state.GameId.ToString("D"));
        command.Parameters.AddWithValue("$last_attempt_at_utc", SqliteTime.Serialize(state.LastAttemptAtUtc));
        command.Parameters.AddWithValue(
            "$last_success_at_utc",
            state.LastSuccessAtUtc is { } success ? SqliteTime.Serialize(success) : DBNull.Value);
        command.Parameters.AddWithValue("$last_status", (int)state.LastStatus);
        command.Parameters.AddWithValue("$last_error_code", (object?)state.LastErrorCode ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_file_count", (object?)state.LastFileCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$last_total_bytes", (object?)state.LastTotalBytes ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$last_changed",
            state.LastChanged is { } changed ? (changed ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$updated_at_utc", SqliteTime.Serialize(state.UpdatedAtUtc));
        var affected = await command.ExecuteNonQueryAsync(cancellationToken);
        if (affected == 0)
        {
            throw new InvalidOperationException("Save Safety state requires an existing game.");
        }
    }

    private static SaveSafetyState Read(Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            SqliteTime.Deserialize(reader.GetString(1)),
            reader.IsDBNull(2) ? null : SqliteTime.Deserialize(reader.GetString(2)),
            (SaveSafetyOperationStatus)reader.GetInt32(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt64(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7) != 0,
            SqliteTime.Deserialize(reader.GetString(8)));

    private static void Validate(SaveSafetyState state)
    {
        if (state.GameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(state));
        if (!Enum.IsDefined(state.LastStatus)) throw new ArgumentOutOfRangeException(nameof(state));
        if (state.LastFileCount is < 0) throw new ArgumentOutOfRangeException(nameof(state));
        if (state.LastTotalBytes is < 0) throw new ArgumentOutOfRangeException(nameof(state));
        if (state.LastSuccessAtUtc > state.LastAttemptAtUtc)
            throw new ArgumentException("Last success cannot be newer than the latest attempt.", nameof(state));
        if (state.UpdatedAtUtc < state.LastAttemptAtUtc)
            throw new ArgumentException("Updated time cannot precede the latest attempt.", nameof(state));

        var failed = state.LastStatus == SaveSafetyOperationStatus.Failed;
        if (failed != !string.IsNullOrWhiteSpace(state.LastErrorCode))
            throw new ArgumentException("Failed operations require an error code and successful operations cannot keep one.", nameof(state));
        if (failed && (state.LastFileCount is not null || state.LastTotalBytes is not null || state.LastChanged is not null))
            throw new ArgumentException("Failed operations cannot claim backup result metrics.", nameof(state));
        if (!failed && (state.LastFileCount is null || state.LastTotalBytes is null || state.LastChanged is null))
            throw new ArgumentException("Completed operations require backup result metrics.", nameof(state));
    }
}
