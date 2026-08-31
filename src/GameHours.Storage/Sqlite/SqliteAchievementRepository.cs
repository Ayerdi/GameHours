using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteAchievementRepository : IAchievementRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteAchievementRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task<bool> HasObservedGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS(
                SELECT 1
                FROM achievement_observation_state
                WHERE game_id = $game_id
            );
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    public async Task<AchievementApplyResult> ApplySnapshotAsync(
        Guid gameId,
        IReadOnlyList<AchievementObservation> observations,
        string source,
        bool hasCompleteCatalogue,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default,
        AchievementStateEvidenceCoverage stateCoverage = AchievementStateEvidenceCoverage.Unknown)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentNullException.ThrowIfNull(observations);
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Achievement source cannot be empty.", nameof(source));
        }

        if (!Enum.IsDefined(stateCoverage))
        {
            throw new ArgumentOutOfRangeException(nameof(stateCoverage), stateCoverage, "Unknown achievement state coverage.");
        }

        var normalizedSource = source.Trim();
        var observedAt = observedAtUtc.ToUniversalTime();
        var normalized = NormalizeObservations(observations);
        var newlyUnlocked = new List<StoredAchievement>();

        await using var connection = _database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var observation in normalized)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existing = await GetOneAsync(
                connection,
                transaction,
                gameId,
                observation.ApiName,
                cancellationToken);

            var merged = Merge(
                gameId,
                existing,
                observation,
                normalizedSource,
                hasCompleteCatalogue,
                observedAt);

            await UpsertAsync(connection, transaction, merged, cancellationToken);

            if (observation.IsUnlocked && existing?.IsUnlocked != true)
            {
                newlyUnlocked.Add(merged);
            }
        }

        await UpsertObservationStateAsync(
            connection,
            transaction,
            gameId,
            normalizedSource,
            hasCompleteCatalogue,
            observedAt,
            stateCoverage,
            cancellationToken);

        if (hasCompleteCatalogue)
        {
            await UpsertCompletionMilestoneIfCompleteAsync(
                connection,
                transaction,
                gameId,
                normalizedSource,
                observedAt,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        var current = await GetForGameAsync(gameId, cancellationToken);
        return new AchievementApplyResult(current, newlyUnlocked);
    }

    public async Task<IReadOnlyList<StoredAchievement>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        var results = new List<StoredAchievement>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id, api_name, display_name, description, hidden, is_unlocked,
                   unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc,
                   first_unlocked_seen_at_utc
            FROM achievement_states
            WHERE game_id = $game_id
            ORDER BY is_unlocked DESC, display_name COLLATE NOCASE, api_name COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadStoredAchievement(reader));
        }

        return results;
    }

    private static IReadOnlyList<AchievementObservation> NormalizeObservations(
        IEnumerable<AchievementObservation> observations)
    {
        return observations
            .Select(observation => observation.Normalize())
            .GroupBy(observation => observation.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var entries = group.ToArray();
                var preferred = entries
                    .OrderByDescending(HasRichMetadata)
                    .ThenByDescending(observation => observation.IsUnlocked)
                    .First();
                var unlocked = entries.Any(observation => observation.IsUnlocked);
                var unlockTimes = entries
                    .Where(observation => observation.IsUnlocked && observation.UnlockedAtUtc is not null)
                    .Select(observation => observation.UnlockedAtUtc!.Value)
                    .ToArray();

                return preferred with
                {
                    IsUnlocked = unlocked,
                    UnlockedAtUtc = unlockTimes.Length == 0 ? null : unlockTimes.Min()
                };
            })
            .ToArray();
    }

    private static StoredAchievement Merge(
        Guid gameId,
        StoredAchievement? existing,
        AchievementObservation observation,
        string source,
        bool hasCompleteCatalogue,
        DateTimeOffset observedAtUtc)
    {
        var wasUnlocked = existing?.IsUnlocked == true;
        var isUnlocked = wasUnlocked || observation.IsUnlocked;
        var unlockedAt = Earliest(
            existing?.UnlockedAtUtc,
            observation.IsUnlocked ? observation.UnlockedAtUtc : null);
        var firstUnlockedSeenAt = existing?.FirstUnlockedSeenAtUtc
            ?? (observation.IsUnlocked ? observedAtUtc : null);

        var useIncomingMetadata = hasCompleteCatalogue || existing is null;
        var displayName = useIncomingMetadata && HasUsefulDisplayName(observation)
            ? observation.DisplayName
            : existing?.DisplayName ?? observation.DisplayName;
        var description = useIncomingMetadata && !string.IsNullOrWhiteSpace(observation.Description)
            ? observation.Description
            : existing?.Description ?? observation.Description;
        var hidden = hasCompleteCatalogue
            ? observation.Hidden
            : existing?.Hidden ?? observation.Hidden;

        return new StoredAchievement(
            gameId,
            observation.ApiName,
            displayName,
            description,
            hidden,
            isUnlocked,
            isUnlocked ? unlockedAt : null,
            source,
            existing?.FirstSeenAtUtc ?? observedAtUtc,
            observedAtUtc,
            firstUnlockedSeenAt);
    }

    private static async Task<StoredAchievement?> GetOneAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid gameId,
        string apiName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            SELECT game_id, api_name, display_name, description, hidden, is_unlocked,
                   unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc,
                   first_unlocked_seen_at_utc
            FROM achievement_states
            WHERE game_id = $game_id AND api_name = $api_name COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$api_name", apiName);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? ReadStoredAchievement(reader)
            : null;
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        StoredAchievement achievement,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO achievement_states(
                game_id, api_name, display_name, description, hidden, is_unlocked,
                unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc,
                first_unlocked_seen_at_utc)
            VALUES(
                $game_id, $api_name, $display_name, $description, $hidden, $is_unlocked,
                $unlocked_at_utc, $source, $first_seen_at_utc, $last_seen_at_utc,
                $first_unlocked_seen_at_utc)
            ON CONFLICT(game_id, api_name) DO UPDATE SET
                display_name = excluded.display_name,
                description = excluded.description,
                hidden = excluded.hidden,
                is_unlocked = excluded.is_unlocked,
                unlocked_at_utc = excluded.unlocked_at_utc,
                source = excluded.source,
                first_seen_at_utc = excluded.first_seen_at_utc,
                last_seen_at_utc = excluded.last_seen_at_utc,
                first_unlocked_seen_at_utc = excluded.first_unlocked_seen_at_utc;
            """;
        command.Parameters.AddWithValue("$game_id", achievement.GameId.ToString("D"));
        command.Parameters.AddWithValue("$api_name", achievement.ApiName);
        command.Parameters.AddWithValue("$display_name", achievement.DisplayName);
        command.Parameters.AddWithValue("$description", achievement.Description);
        command.Parameters.AddWithValue("$hidden", achievement.Hidden ? 1 : 0);
        command.Parameters.AddWithValue("$is_unlocked", achievement.IsUnlocked ? 1 : 0);
        command.Parameters.AddWithValue(
            "$unlocked_at_utc",
            achievement.UnlockedAtUtc is null
                ? DBNull.Value
                : SqliteTime.Serialize(achievement.UnlockedAtUtc.Value));
        command.Parameters.AddWithValue("$source", achievement.Source);
        command.Parameters.AddWithValue("$first_seen_at_utc", SqliteTime.Serialize(achievement.FirstSeenAtUtc));
        command.Parameters.AddWithValue("$last_seen_at_utc", SqliteTime.Serialize(achievement.LastSeenAtUtc));
        command.Parameters.AddWithValue(
            "$first_unlocked_seen_at_utc",
            achievement.FirstUnlockedSeenAtUtc is null
                ? DBNull.Value
                : SqliteTime.Serialize(achievement.FirstUnlockedSeenAtUtc.Value));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertObservationStateAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid gameId,
        string source,
        bool hasCompleteCatalogue,
        DateTimeOffset observedAtUtc,
        AchievementStateEvidenceCoverage stateCoverage,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            INSERT INTO achievement_observation_state(
                game_id, initialized_at_utc, last_observed_at_utc, last_source,
                has_complete_catalogue, state_coverage)
            VALUES(
                $game_id, $observed_at_utc, $observed_at_utc, $source,
                $has_complete_catalogue, $state_coverage)
            ON CONFLICT(game_id) DO UPDATE SET
                last_observed_at_utc = excluded.last_observed_at_utc,
                last_source = excluded.last_source,
                has_complete_catalogue = MAX(
                    achievement_observation_state.has_complete_catalogue,
                    excluded.has_complete_catalogue),
                state_coverage = excluded.state_coverage;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$observed_at_utc", SqliteTime.Serialize(observedAtUtc));
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$has_complete_catalogue", hasCompleteCatalogue ? 1 : 0);
        command.Parameters.AddWithValue("$state_coverage", (int)stateCoverage);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpsertCompletionMilestoneIfCompleteAsync(
        SqliteConnection connection,
        System.Data.Common.DbTransaction transaction,
        Guid gameId,
        string source,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        long knownCount;
        long unlockedCount;
        string? completedAtText;

        await using (var summaryCommand = connection.CreateCommand())
        {
            summaryCommand.Transaction = (SqliteTransaction)transaction;
            summaryCommand.CommandText = """
                SELECT COUNT(*),
                       COALESCE(SUM(CASE WHEN is_unlocked = 1 THEN 1 ELSE 0 END), 0),
                       MAX(CASE
                           WHEN is_unlocked = 1
                           THEN COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc)
                           ELSE NULL
                       END)
                FROM achievement_states
                WHERE game_id = $game_id;
                """;
            summaryCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

            await using var reader = await summaryCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return;
            }

            knownCount = reader.GetInt64(0);
            unlockedCount = reader.GetInt64(1);
            completedAtText = reader.IsDBNull(2) ? null : reader.GetString(2);
        }

        if (knownCount == 0 || unlockedCount < knownCount || string.IsNullOrWhiteSpace(completedAtText))
        {
            return;
        }

        var completedAtUtc = SqliteTime.Deserialize(completedAtText);
        bool isObservedTimeFallback;
        await using (var fallbackCommand = connection.CreateCommand())
        {
            fallbackCommand.Transaction = (SqliteTransaction)transaction;
            fallbackCommand.CommandText = """
                SELECT COALESCE(MAX(CASE WHEN unlocked_at_utc IS NULL THEN 1 ELSE 0 END), 0)
                FROM achievement_states
                WHERE game_id = $game_id
                  AND is_unlocked = 1
                  AND COALESCE(unlocked_at_utc, first_unlocked_seen_at_utc) = $completed_at_utc;
                """;
            fallbackCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
            fallbackCommand.Parameters.AddWithValue("$completed_at_utc", completedAtText);
            var value = await fallbackCommand.ExecuteScalarAsync(cancellationToken);
            isObservedTimeFallback = Convert.ToInt64(
                value,
                System.Globalization.CultureInfo.InvariantCulture) != 0;
        }

        await using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = (SqliteTransaction)transaction;
        insertCommand.CommandText = """
            INSERT INTO achievement_completion_milestones(
                game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc)
            VALUES(
                $game_id, $completed_at_utc, $is_fallback, $source, $recorded_at_utc)
            ON CONFLICT(game_id) DO UPDATE SET
                completed_at_utc = excluded.completed_at_utc,
                is_observed_time_fallback = excluded.is_observed_time_fallback,
                source = excluded.source,
                recorded_at_utc = excluded.recorded_at_utc
            WHERE achievement_completion_milestones.is_observed_time_fallback = 1
              AND excluded.is_observed_time_fallback = 0;
            """;
        insertCommand.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        insertCommand.Parameters.AddWithValue("$completed_at_utc", SqliteTime.Serialize(completedAtUtc));
        insertCommand.Parameters.AddWithValue("$is_fallback", isObservedTimeFallback ? 1 : 0);
        insertCommand.Parameters.AddWithValue("$source", source);
        insertCommand.Parameters.AddWithValue("$recorded_at_utc", SqliteTime.Serialize(observedAtUtc));
        await insertCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StoredAchievement ReadStoredAchievement(SqliteDataReader reader) =>
        new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.GetInt64(5) != 0,
            reader.IsDBNull(6) ? null : SqliteTime.Deserialize(reader.GetString(6)),
            reader.GetString(7),
            SqliteTime.Deserialize(reader.GetString(8)),
            SqliteTime.Deserialize(reader.GetString(9)),
            reader.IsDBNull(10) ? null : SqliteTime.Deserialize(reader.GetString(10)));

    private static bool HasRichMetadata(AchievementObservation observation) =>
        HasUsefulDisplayName(observation) || !string.IsNullOrWhiteSpace(observation.Description);

    private static bool HasUsefulDisplayName(AchievementObservation observation) =>
        !string.IsNullOrWhiteSpace(observation.DisplayName) &&
        !string.Equals(observation.DisplayName, observation.ApiName, StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return left <= right ? left : right;
    }
}
