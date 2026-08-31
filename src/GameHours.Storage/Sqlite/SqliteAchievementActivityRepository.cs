using GameHours.Core.Domain;

namespace GameHours.Storage.Sqlite;

/// <summary>
/// Read model for durable achievement state. These queries never inspect external achievement
/// files; they operate only on GameHours normalized SQLite persistence.
/// </summary>
public sealed class SqliteAchievementActivityRepository
{
    private const int MaxRecentItems = 500;
    private const string GseSourcePattern = "%GSE/Goldberg%";
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
                        AND NOT (
                            source LIKE $gse_source_pattern COLLATE NOCASE
                            AND first_unlocked_seen_at_utc = first_seen_at_utc)
                       THEN COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc)
                       ELSE NULL
                   END),
                   MAX(CASE
                       WHEN is_unlocked = 1
                        AND NOT (
                            source LIKE $gse_source_pattern COLLATE NOCASE
                            AND first_unlocked_seen_at_utc = first_seen_at_utc)
                       THEN COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc)
                       ELSE NULL
                   END),
                   COALESCE(SUM(CASE
                       WHEN is_unlocked = 1
                        AND source LIKE $gse_source_pattern COLLATE NOCASE
                        AND first_unlocked_seen_at_utc = first_seen_at_utc
                       THEN 1 ELSE 0
                   END), 0)
            FROM achievement_states
            WHERE game_id = $game_id;
            """;
        stateCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        stateCommand.Parameters.AddWithValue("$gse_source_pattern", GseSourcePattern);

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
        var hasUnverifiedHistoricalTimes = stateReader.GetInt64(4) > 0;
        DateTimeOffset? firstUnlockedAtUtc = hasUnverifiedHistoricalTimes || stateReader.IsDBNull(2)
            ? null
            : SqliteTime.Deserialize(stateReader.GetString(2));
        DateTimeOffset? lastUnlockedAtUtc = stateReader.IsDBNull(3)
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
        ValidateRecentLimit(limit);
        ValidateOptionalGameId(gameId);

        var results = new List<AchievementUnlockActivity>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source, a.first_seen_at_utc
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.is_unlocked = 1
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END IS NOT NULL
                ORDER BY CASE
                    WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                     AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                    THEN a.first_unlocked_seen_at_utc
                    ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                END DESC,
                a.first_unlocked_seen_at_utc DESC,
                a.api_name COLLATE NOCASE
                LIMIT $limit;
                """
            : """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source, a.first_seen_at_utc
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.game_id = $game_id
                  AND a.is_unlocked = 1
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END IS NOT NULL
                ORDER BY CASE
                    WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                     AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                    THEN a.first_unlocked_seen_at_utc
                    ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                END DESC,
                a.first_unlocked_seen_at_utc DESC,
                a.api_name COLLATE NOCASE
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$gse_source_pattern", GseSourcePattern);
        AddOptionalGameId(command, gameId);

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
        ValidateRange(fromUtc, toUtc);
        ValidateOptionalGameId(gameId);

        var results = new List<AchievementUnlockActivity>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source, a.first_seen_at_utc
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.is_unlocked = 1
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END >= $from_utc
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END < $to_utc
                ORDER BY CASE
                    WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                     AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                    THEN a.first_unlocked_seen_at_utc
                    ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                END,
                a.first_unlocked_seen_at_utc,
                a.api_name COLLATE NOCASE;
                """
            : """
                SELECT a.game_id, g.title, a.api_name, a.display_name, a.description, a.hidden,
                       a.unlocked_at_utc, a.first_unlocked_seen_at_utc, a.source, a.first_seen_at_utc
                FROM achievement_states a
                JOIN games g ON g.id = a.game_id
                WHERE a.game_id = $game_id
                  AND a.is_unlocked = 1
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END >= $from_utc
                  AND CASE
                      WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                       AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                      THEN a.first_unlocked_seen_at_utc
                      ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                  END < $to_utc
                ORDER BY CASE
                    WHEN a.source LIKE $gse_source_pattern COLLATE NOCASE
                     AND a.first_unlocked_seen_at_utc = a.first_seen_at_utc
                    THEN a.first_unlocked_seen_at_utc
                    ELSE COALESCE(a.unlocked_at_utc, a.first_unlocked_seen_at_utc)
                END,
                a.first_unlocked_seen_at_utc,
                a.api_name COLLATE NOCASE;
                """;
        command.Parameters.AddWithValue("$from_utc", SqliteTime.Serialize(fromUtc));
        command.Parameters.AddWithValue("$to_utc", SqliteTime.Serialize(toUtc));
        command.Parameters.AddWithValue("$gse_source_pattern", GseSourcePattern);
        AddOptionalGameId(command, gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadUnlockActivity(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<AchievementCompletionMilestone>> GetRecentCompletionMilestonesAsync(
        int limit = 50,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRecentLimit(limit);
        ValidateOptionalGameId(gameId);

        var results = new List<AchievementCompletionMilestone>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT m.game_id, g.title, m.completed_at_utc,
                       m.is_observed_time_fallback, m.source
                FROM achievement_completion_milestones m
                JOIN games g ON g.id = m.game_id
                ORDER BY m.completed_at_utc DESC, g.title COLLATE NOCASE
                LIMIT $limit;
                """
            : """
                SELECT m.game_id, g.title, m.completed_at_utc,
                       m.is_observed_time_fallback, m.source
                FROM achievement_completion_milestones m
                JOIN games g ON g.id = m.game_id
                WHERE m.game_id = $game_id
                ORDER BY m.completed_at_utc DESC, g.title COLLATE NOCASE
                LIMIT $limit;
                """;
        command.Parameters.AddWithValue("$limit", limit);
        AddOptionalGameId(command, gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCompletionMilestone(reader));
        }

        return results;
    }

    public async Task<IReadOnlyList<AchievementCompletionMilestone>> GetCompletionMilestonesAsync(
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRange(fromUtc, toUtc);
        ValidateOptionalGameId(gameId);

        var results = new List<AchievementCompletionMilestone>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = gameId is null
            ? """
                SELECT m.game_id, g.title, m.completed_at_utc,
                       m.is_observed_time_fallback, m.source
                FROM achievement_completion_milestones m
                JOIN games g ON g.id = m.game_id
                WHERE m.completed_at_utc >= $from_utc
                  AND m.completed_at_utc < $to_utc
                ORDER BY m.completed_at_utc, g.title COLLATE NOCASE;
                """
            : """
                SELECT m.game_id, g.title, m.completed_at_utc,
                       m.is_observed_time_fallback, m.source
                FROM achievement_completion_milestones m
                JOIN games g ON g.id = m.game_id
                WHERE m.game_id = $game_id
                  AND m.completed_at_utc >= $from_utc
                  AND m.completed_at_utc < $to_utc
                ORDER BY m.completed_at_utc, g.title COLLATE NOCASE;
                """;
        command.Parameters.AddWithValue("$from_utc", SqliteTime.Serialize(fromUtc));
        command.Parameters.AddWithValue("$to_utc", SqliteTime.Serialize(toUtc));
        AddOptionalGameId(command, gameId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCompletionMilestone(reader));
        }

        return results;
    }

    private static AchievementUnlockActivity ReadUnlockActivity(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var firstUnlockedSeenAtUtc = reader.IsDBNull(7)
            ? (DateTimeOffset?)null
            : SqliteTime.Deserialize(reader.GetString(7));
        var source = reader.GetString(8);
        var firstSeenAtUtc = SqliteTime.Deserialize(reader.GetString(9));
        var historicalTimeUnverified = AchievementUnlockTimePolicy.IsHistoricalTimeUnverified(
            source,
            firstSeenAtUtc,
            firstUnlockedSeenAtUtc);
        var hasSourceUnlockTime = !reader.IsDBNull(6) && !historicalTimeUnverified;
        var occurredAtUtc = hasSourceUnlockTime
            ? SqliteTime.Deserialize(reader.GetString(6))
            : firstUnlockedSeenAtUtc ?? SqliteTime.Deserialize(reader.GetString(6));

        return new AchievementUnlockActivity(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt64(5) != 0,
            occurredAtUtc,
            IsObservedTimeFallback: !hasSourceUnlockTime,
            source);
    }

    private static AchievementCompletionMilestone ReadCompletionMilestone(
        Microsoft.Data.Sqlite.SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            SqliteTime.Deserialize(reader.GetString(2)),
            reader.GetInt64(3) != 0,
            reader.GetString(4));

    private static void ValidateRecentLimit(int limit)
    {
        if (limit is < 1 or > MaxRecentItems)
        {
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"Recent achievement limit must be between 1 and {MaxRecentItems}.");
        }
    }

    private static void ValidateRange(DateTimeOffset fromUtc, DateTimeOffset toUtc)
    {
        if (toUtc <= fromUtc)
        {
            throw new ArgumentException("Achievement activity range must have a positive duration.");
        }
    }

    private static void ValidateOptionalGameId(Guid? gameId)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty when supplied.", nameof(gameId));
        }
    }

    private static void AddOptionalGameId(
        Microsoft.Data.Sqlite.SqliteCommand command,
        Guid? gameId)
    {
        if (gameId is Guid filteredGameId)
        {
            command.Parameters.AddWithValue("$game_id", filteredGameId.ToString("D"));
        }
    }
}
