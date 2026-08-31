using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteAchievementEvidenceRepository : IAchievementEvidenceRepository
{
    private const int GameQueryBatchSize = 500;
    private readonly GameHoursDatabase _database;

    public SqliteAchievementEvidenceRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task SaveAsync(
        Guid gameId,
        IReadOnlyList<ConfirmedAchievementUnlockEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.Count == 0)
        {
            return;
        }

        foreach (var proof in evidence)
        {
            ArgumentNullException.ThrowIfNull(proof);
            if (proof.GameId != gameId)
            {
                throw new ArgumentException(
                    $"Evidence for game {proof.GameId:D} cannot be stored under {gameId:D}.",
                    nameof(evidence));
            }
        }

        await using var connection = _database.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var proof in evidence)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertAsync(
                connection,
                (SqliteTransaction)transaction,
                proof,
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StoredAchievementUnlockEvidence>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        ValidateGameId(gameId);
        var byGame = await GetForGamesAsync([gameId], cancellationToken);
        return byGame[gameId];
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<StoredAchievementUnlockEvidence>>> GetForGamesAsync(
        IReadOnlyCollection<Guid> gameIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(gameIds);
        var ids = gameIds.Distinct().ToArray();
        foreach (var gameId in ids)
        {
            ValidateGameId(gameId);
        }

        var results = ids.ToDictionary(id => id, _ => new List<StoredAchievementUnlockEvidence>());
        if (ids.Length == 0)
        {
            return results.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<StoredAchievementUnlockEvidence>)pair.Value);
        }

        await using var connection = _database.OpenConnection();
        foreach (var batch in ids.Chunk(GameQueryBatchSize))
        {
            await using var command = connection.CreateCommand();
            var parameters = batch.Select((_, index) => $"$game_id_{index}").ToArray();
            command.CommandText = $"""
                SELECT game_id, api_name, origin, provider, rule_id, rule_version,
                       source_path, source_fingerprint, detail,
                       first_observed_at_utc, last_observed_at_utc
                FROM achievement_unlock_evidence
                WHERE game_id IN ({string.Join(", ", parameters)})
                ORDER BY game_id,
                         api_name COLLATE NOCASE,
                         provider COLLATE NOCASE,
                         rule_id COLLATE NOCASE,
                         rule_version,
                         source_path COLLATE NOCASE;
                """;
            for (var index = 0; index < batch.Length; index++)
            {
                command.Parameters.AddWithValue(parameters[index], batch[index].ToString("D"));
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var stored = Read(reader);
                results[stored.GameId].Add(stored);
            }
        }

        return results.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<StoredAchievementUnlockEvidence>)pair.Value);
    }

    private static async Task UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ConfirmedAchievementUnlockEvidence proof,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO achievement_unlock_evidence(
                game_id, api_name, origin, provider, rule_id, rule_version,
                source_path, source_fingerprint, detail,
                first_observed_at_utc, last_observed_at_utc)
            VALUES(
                $game_id, $api_name, $origin, $provider, $rule_id, $rule_version,
                $source_path, $source_fingerprint, $detail,
                $observed_at_utc, $observed_at_utc)
            ON CONFLICT(game_id, api_name, provider, rule_id, rule_version, source_path)
            DO UPDATE SET
                origin = CASE
                    WHEN excluded.last_observed_at_utc >= achievement_unlock_evidence.last_observed_at_utc
                    THEN excluded.origin ELSE achievement_unlock_evidence.origin END,
                source_fingerprint = CASE
                    WHEN excluded.last_observed_at_utc >= achievement_unlock_evidence.last_observed_at_utc
                    THEN excluded.source_fingerprint ELSE achievement_unlock_evidence.source_fingerprint END,
                detail = CASE
                    WHEN excluded.last_observed_at_utc >= achievement_unlock_evidence.last_observed_at_utc
                    THEN excluded.detail ELSE achievement_unlock_evidence.detail END,
                first_observed_at_utc = MIN(
                    achievement_unlock_evidence.first_observed_at_utc,
                    excluded.first_observed_at_utc),
                last_observed_at_utc = MAX(
                    achievement_unlock_evidence.last_observed_at_utc,
                    excluded.last_observed_at_utc);
            """;
        command.Parameters.AddWithValue("$game_id", proof.GameId.ToString("D"));
        command.Parameters.AddWithValue("$api_name", proof.ApiName);
        command.Parameters.AddWithValue("$origin", (int)proof.Origin);
        command.Parameters.AddWithValue("$provider", proof.Provider);
        command.Parameters.AddWithValue("$rule_id", proof.RuleId);
        command.Parameters.AddWithValue("$rule_version", proof.RuleVersion);
        command.Parameters.AddWithValue("$source_path", NormalizeSourcePath(proof.SourcePath));
        command.Parameters.AddWithValue(
            "$source_fingerprint",
            proof.SourceFingerprint is null ? DBNull.Value : proof.SourceFingerprint);
        command.Parameters.AddWithValue("$detail", proof.Detail);
        command.Parameters.AddWithValue("$observed_at_utc", SqliteTime.Serialize(proof.ObservedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static StoredAchievementUnlockEvidence Read(SqliteDataReader reader)
    {
        var originValue = reader.GetInt32(2);
        if (!Enum.IsDefined(typeof(AchievementEvidenceOrigin), originValue))
        {
            throw new InvalidDataException($"Unknown achievement evidence origin {originValue} in database.");
        }

        return new StoredAchievementUnlockEvidence(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            (AchievementEvidenceOrigin)originValue,
            reader.GetString(3),
            reader.GetString(4),
            reader.GetInt32(5),
            string.IsNullOrEmpty(reader.GetString(6)) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetString(8),
            SqliteTime.Deserialize(reader.GetString(9)),
            SqliteTime.Deserialize(reader.GetString(10)));
    }

    private static string NormalizeSourcePath(string? sourcePath) =>
        string.IsNullOrWhiteSpace(sourcePath) ? string.Empty : sourcePath.Trim();

    private static void ValidateGameId(Guid gameId)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }
    }
}
