using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteExecutableMappingRepository : IExecutableMappingRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteExecutableMappingRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<ExecutableMapping?> FindByPathAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        var normalized = Path.GetFullPath(executablePath);
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, executable_path, is_helper
            FROM executable_mappings
            WHERE executable_path = $path COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$path", normalized);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExecutableMapping(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetInt32(2) != 0);
    }

    public async Task<IReadOnlyList<ExecutableMapping>> GetForGameAsync(
        Guid gameId,
        bool includeHelpers = false,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        var results = new List<ExecutableMapping>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = includeHelpers
            ? """
                SELECT game_id, executable_path, is_helper
                FROM executable_mappings
                WHERE game_id = $gameId
                ORDER BY is_helper, executable_path COLLATE NOCASE;
                """
            : """
                SELECT game_id, executable_path, is_helper
                FROM executable_mappings
                WHERE game_id = $gameId AND is_helper = 0
                ORDER BY executable_path COLLATE NOCASE;
                """;
        command.Parameters.AddWithValue("$gameId", gameId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ExecutableMapping(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetInt32(2) != 0));
        }

        return results;
    }

    public async Task UpsertAsync(
        ExecutableMapping mapping,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO executable_mappings(
                id, game_id, executable_path, executable_name, is_helper, created_at_utc)
            VALUES($id, $gameId, $path, $name, $isHelper, $now)
            ON CONFLICT(executable_path) DO UPDATE SET
                game_id = excluded.game_id,
                executable_name = excluded.executable_name,
                is_helper = excluded.is_helper;
            """;
        command.Parameters.AddWithValue(
            "$id",
            DeterministicGameId.Create("executable-mapping", mapping.ExecutablePath).ToString("D"));
        command.Parameters.AddWithValue("$gameId", mapping.GameId.ToString("D"));
        command.Parameters.AddWithValue("$path", mapping.ExecutablePath);
        command.Parameters.AddWithValue("$name", mapping.ExecutableName);
        command.Parameters.AddWithValue("$isHelper", mapping.IsHelper ? 1 : 0);
        command.Parameters.AddWithValue("$now", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
