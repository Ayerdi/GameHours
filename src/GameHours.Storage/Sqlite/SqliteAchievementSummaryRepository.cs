using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteAchievementSummaryRepository
{
    private readonly GameHoursDatabase _database;
    public SqliteAchievementSummaryRepository(GameHoursDatabase database) => _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<IReadOnlyDictionary<Guid, AchievementGameSummary>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<Guid, AchievementGameSummary>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT o.game_id,
                   COUNT(s.api_name),
                   COALESCE(SUM(CASE WHEN s.is_unlocked = 1 THEN 1 ELSE 0 END), 0),
                   o.has_complete_catalogue,
                   MIN(CASE WHEN s.is_unlocked = 1 THEN COALESCE(s.unlocked_at_utc, s.first_unlocked_seen_at_utc) END),
                   MAX(CASE WHEN s.is_unlocked = 1 THEN COALESCE(s.unlocked_at_utc, s.first_unlocked_seen_at_utc) END),
                   o.last_observed_at_utc,
                   o.last_source
            FROM achievement_observation_state o
            LEFT JOIN achievement_states s ON s.game_id = o.game_id
            GROUP BY o.game_id, o.has_complete_catalogue, o.last_observed_at_utc, o.last_source;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var gameId = Guid.Parse(reader.GetString(0));
            result[gameId] = new AchievementGameSummary(
                gameId,
                checked((int)reader.GetInt64(1)),
                checked((int)reader.GetInt64(2)),
                reader.GetInt64(3) != 0,
                reader.IsDBNull(4) ? null : SqliteTime.Deserialize(reader.GetString(4)),
                reader.IsDBNull(5) ? null : SqliteTime.Deserialize(reader.GetString(5)),
                SqliteTime.Deserialize(reader.GetString(6)),
                reader.GetString(7));
        }
        return result;
    }
}
