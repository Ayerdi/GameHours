using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

/// <summary>
/// Read model for durable achievement state. These queries never inspect external achievement
/// files; they operate only on GameHours normalized SQLite persistence.
/// </summary>
public sealed class SqliteAchievementActivityRepository
{
    private const int MaxRecentItems = 500;
    private readonly GameHoursDatabase _database;

    public SqliteAchievementActivityRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<AchievementGameSummary?> GetSummaryAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        await using var connection = _database.OpenConnection();

        bool hasObservation;
        bool hasCompleteCatalogue;
        DateTimeOffset? lastObservedAtUtc;
        string? lastSource;
        await using (var observationCommand = connection.CreateCommand())
        {
            observationCommand.CommandText = """
                SELECT last_observed_at_utc, last_source, has_complete_catalogue
                FROM achievement_observation_state
                WHERE game_id = $game_id
                LIMIT 1;
                """;
            observationCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

            await using var reader = await observationCommand.ExecuteReaderAsync(cancellationToken);
            hasObservation = await reader.ReadAsync(cancellationToken);
            if (hasObservation)
            {
                lastObservedAtUtc = SqliteTime.Deserialize(reader.GetString(0));
                lastSource = reader.GetString(1);
                hasCompleteCatalogue = reader.GetInt64(2) != 0;
            }
            else
            {
                lastObservedAtUtc = null;
                lastSource = null;
                hasCompleteCatalogue = false;
            }
        }

        if (!hasObservation)
        {
            return null;
        }

        await using var stateCommand = connection.CreateCommand();
        stateCommand.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(CASE WHEN is_unlocked = 1 THEN 1 ELSE 0 END), 0),
                   MIN(CASE
                       WHEN is_unlocked = 1
                       THEN COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc)
                       ELSE NULL
                   END),
                   MAX(CASE
                       WHEN is_unlocked = 1
                       THEN COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc)
                       ELSE NULL
                   END)
            FROM achievement_states
            WHERE game_id = $game_id;
            """;
        stateCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

        await using var stateReader = await stateCommand.ExecuteReaderAsync(cancellationToken);
        if (!await stateReader.ReadAsync(cancellationToken))
        {
            return new AchievementGameSummary(
                gameId,
                0,
                0,
                hasCompleteCatalogue,
                null,
                null,
                lastObservedAtUtc,
                lastSource);
        }

        var knownCount = checked((int)stateReader.GetInt64(0));
        var unlockedCount = checked((int)stateReader.GetInt64(1));
        var firstUnlockedAtUtc = stateReader.IsDBNull(2)
            ? null
            : SqliteTime.Deserialize(stateReader.GetString(2));
        var lastUnlockedAtUtc = stateReader.IsDBNull(3)
            ? null
            : SqliteTime.Deserialize(stateReader.GetString(3));

        return new AchievementGameSummary(
            gameId,
            knownCount,
            unlockedCount,
            hasCompleteCatalogue,
            firstUnlockedAtUtc,
            lastUnlockedAtUtc,
            lastObservedAtUtc,
            lastSource);
    }

    public async Task<IReadOnlyList<AchievementUnlockActivity>> GetRecentUnlocksAsync(
        int limit = 50,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > MaxRecentItems)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Recent achievement limit must be between 1 and {MaxRecentItems}.");
        }

        ValidateOptionalGameId(gameId);

        var results = new List<AchievementUnlockActivity>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.is_unlocked = 1
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) IS NOT NULL
                ORDER BY COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) DESC,
                         a.first_unlocked_seen_at_utc DESC,
                         a.api_name COLLATE NOCASE
                LIMIT $limit;
                """
            : """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.game_id = $game_id
                  AND a.is_unlocked = 1
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) IS NOT NULL
                ORDER BY COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) DESC,
                         a.first_unlocked_seen_at_utc DESC,
                         a.api_name COLLATE NOCASE
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$limit", limit);
        if (gameId is Guid filteredGameId)
        {
            command.Parameters.AddWithValue("$game_id", filteredGameId.ToString("D"));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadUnlockActivity(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<AchievementUnlockActivity>> GetUnlocksAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Achievement activity range must have a positive duration.");
        }

        ValidateOptionalGameId(gameId);

        var results = new List<AchievementUnlockActivity>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.is_unlocked = 1
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) >= $from_utc
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) < $to_utc
                ORDER BY COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc),
                         a.first_unlocked_seen_at_utc,
                         a.api_name COLLATE NOCASE;
                """
            : """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.game_id = $game_id
                  AND a.is_unlocked = 1
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) >= $from_utc
                  AND COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc) < $to_utc
                ORDER BY COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc),
                         a.first_unlocked_seen_at_utc,
                         a.api_name COLLATE NOCASE;
                """;
        command.Parameters.AddWithValue("$from_utc", SqliteTime.Serialize(fromUtc));
        command.Parameters.AddWithValue("$to_utc", SqliteTime.Serialize(toUtc));
        if (gameId is Guid filteredGameId)
        {
            command.Parameters.AddWithValue("$game_id", filteredGameId.ToString("D"));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadUnlockActivity(reader));
        }

        return results;
    }

    private static AchievementUnlockActivity ReadUnlockActivity(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var hasSourceUnlockTime = !reader.IsDBNull(6);
        var occurredAtUtc = hasSourceUnlockTime
            ? SqliteTime.Deserialize(reader.GetString(6))
            : SqliteTime.Deserialize(reader.GetString(7));

        return new AchievementUnlockActivity(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) != 0,
            occurredAtUtc,
            IsObservedTimeFallback: !hasSourceUnlockTime,
            reader.GetString(8));
    }

    private static void ValidateOptionalGameId(Guid? gameId)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty when supplied.", nameof(gameId));
        }
    }
}
