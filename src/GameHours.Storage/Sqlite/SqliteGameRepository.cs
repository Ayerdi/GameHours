using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteGameRepository : IGameRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteGameRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES($id, $title, NULL, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                title = excluded.title,
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$id", game.Id.ToString("D"));
        command.Parameters.AddWithValue("$title", game.Title);
        command.Parameters.AddWithValue("$now", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<TrackedGame>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title FROM games ORDER BY title COLLATE NOCASE;";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new TrackedGame(Guid.Parse(reader.GetString(0)), reader.GetString(1)));
        }

        return results;
    }
}
