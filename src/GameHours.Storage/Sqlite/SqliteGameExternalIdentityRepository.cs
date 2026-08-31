using GameHours.Core.Domain;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteGameExternalIdentityRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteGameExternalIdentityRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task UpsertAsync(
        Guid gameId,
        GameExternalIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateGameId(gameId);
        ArgumentNullException.ThrowIfNull(identity);

        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertCoreAsync(connection, transaction, gameId, identity, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task UpsertManyAsync(
        IEnumerable<(Guid GameId, GameExternalIdentity Identity)> links,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(links);
        var materialized = links.ToArray();
        if (materialized.Length == 0)
        {
            return;
        }

        foreach (var link in materialized)
        {
            ValidateGameId(link.GameId);
            ArgumentNullException.ThrowIfNull(link.Identity);
        }

        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        foreach (var link in materialized)
        {
            await UpsertCoreAsync(connection, transaction, link.GameId, link.Identity, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameExternalIdentity>> GetForGameAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        ValidateGameId(gameId);
        var result = new List<GameExternalIdentity>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT provider, external_id
            FROM game_external_identities
            WHERE game_id = $game_id
            ORDER BY provider COLLATE NOCASE, external_id COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new GameExternalIdentity(reader.GetString(0), reader.GetString(1)));
        }

        return result;
    }

    public async Task<Guid?> FindGameIdAsync(
        GameExternalIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT game_id
            FROM game_external_identities
            WHERE provider = $provider COLLATE NOCASE
              AND external_id = $external_id COLLATE NOCASE
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$provider", identity.Provider);
        command.Parameters.AddWithValue("$external_id", identity.ExternalId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string text ? Guid.Parse(text) : null;
    }

    private static async Task UpsertCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid gameId,
        GameExternalIdentity identity,
        CancellationToken cancellationToken)
    {
        await using (var ownership = connection.CreateCommand())
        {
            ownership.Transaction = transaction;
            ownership.CommandText = """
                SELECT game_id
                FROM game_external_identities
                WHERE provider = $provider COLLATE NOCASE
                  AND external_id = $external_id COLLATE NOCASE
                LIMIT 1;
                """;
            ownership.Parameters.AddWithValue("$provider", identity.Provider);
            ownership.Parameters.AddWithValue("$external_id", identity.ExternalId);
            var existing = await ownership.ExecuteScalarAsync(cancellationToken);
            if (existing is string existingGameId &&
                !string.Equals(existingGameId, gameId.ToString("D"), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"External game identity {identity.Provider}:{identity.ExternalId} is already linked to another GameHours game.");
            }
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO game_external_identities(game_id, provider, external_id, updated_at_utc)
            VALUES($game_id, $provider, $external_id, $updated_at_utc)
            ON CONFLICT(game_id, provider, external_id) DO UPDATE SET
                updated_at_utc = excluded.updated_at_utc;
            """;
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$provider", identity.Provider);
        command.Parameters.AddWithValue("$external_id", identity.ExternalId);
        command.Parameters.AddWithValue("$updated_at_utc", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void ValidateGameId(Guid gameId)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }
    }
}
