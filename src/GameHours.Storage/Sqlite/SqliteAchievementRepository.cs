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

    public async Task<AchievementApplyResult> ApplySnapshotAsync(
        Guid gameId,
        IReadOnlyList<AchievementObservation> observations,
        string source,
        bool hasCompleteCatalogue,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken = default)
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
