using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteLibraryGamePreferencesRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteLibraryGamePreferencesRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<IReadOnlyDictionary<Guid, LibraryGamePreferences>> GetAllAsync(
        CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, LibraryGamePreferences>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, is_favorite, is_hidden, completion_status
            FROM game_library_preferences;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var gameId = Guid.Parse(reader.GetString(0));
            result[gameId] = new LibraryGamePreferences(
                gameId,
                reader.GetInt64(1) != 0,
                reader.GetInt64(2) != 0,
                ReadCompletionStatus(reader.GetInt32(3)));
        }

        return result;
    }

    public async Task<LibraryGamePreferences> GetAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT is_favorite, is_hidden, completion_status
            FROM game_library_preferences
            WHERE game_id = $game_id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new LibraryGamePreferences(gameId);
        }

        return new LibraryGamePreferences(
            gameId,
            reader.GetInt64(0) != 0,
            reader.GetInt64(1) != 0,
            ReadCompletionStatus(reader.GetInt32(2)));
    }

    public async Task SetAsync(
        LibraryGamePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        if (preferences.GameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(preferences));
        }

        ValidateCompletionStatus(preferences.CompletionStatus);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        if (preferences.IsDefault)
        {
            command.CommandText = "DELETE FROM game_library_preferences WHERE game_id = $game_id;";
            command.Parameters.AddWithValue("$game_id", preferences.GameId.ToString("D"));
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        command.CommandText = """
            INSERT INTO game_library_preferences(
                game_id, is_favorite, is_hidden, completion_status, updated_at_utc)
            VALUES($game_id, $is_favorite, $is_hidden, $completion_status, $updated_at_utc)
            ON CONFLICT(game_id) DO UPDATE SET
                is_favorite = excluded.is_favorite,
                is_hidden = excluded.is_hidden,
                completion_status = excluded.completion_status,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$game_id", preferences.GameId.ToString("D"));
        command.Parameters.AddWithValue("$is_favorite", preferences.IsFavorite ? 1 : 0);
        command.Parameters.AddWithValue("$is_hidden", preferences.IsHidden ? 1 : 0);
        command.Parameters.AddWithValue("$completion_status", (int)preferences.CompletionStatus);
        command.Parameters.AddWithValue("$updated_at_utc", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static LibraryCompletionStatus ReadCompletionStatus(int value)
    {
        var status = (LibraryCompletionStatus)value;
        ValidateCompletionStatus(status);
        return status;
    }

    private static void ValidateCompletionStatus(LibraryCompletionStatus status)
    {
        if (!Enum.IsDefined(status))
        {
            throw new InvalidDataException($"Unsupported library completion status: {(int)status}.");
        }
    }
}
