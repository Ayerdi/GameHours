using GameHours.Core.Abstractions;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteTrackingStateRepository : ITrackingStateRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteTrackingStateRepository(GameHoursDatabase database)
    {
        _database = database;
    }

    public async Task<DateTimeOffset?> GetTrackingStartedAtAsync(
        CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is string timestamp ? SqliteTime.Deserialize(timestamp) : null;
    }

    public async Task<DateTimeOffset> GetOrSetTrackingStartedAtAsync(
        DateTimeOffset proposedUtc,
        CancellationToken cancellationToken = default)
    {
        var normalized = proposedUtc.ToUniversalTime();
        await using var connection = _database.OpenConnection();
        using var transaction = connection.BeginTransaction();

        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT OR IGNORE INTO tracking_state(singleton_id, tracking_started_at_utc)
            VALUES (1, $trackingStartedAtUtc);
            """;
        insert.Parameters.AddWithValue("$trackingStartedAtUtc", SqliteTime.Serialize(normalized));
        await insert.ExecuteNonQueryAsync(cancellationToken);

        await using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id = 1;";
        var storedValue = (string?)await select.ExecuteScalarAsync(cancellationToken)
            ?? throw new InvalidOperationException("Tracking cutover was not persisted.");

        transaction.Commit();
        return SqliteTime.Deserialize(storedValue);
    }
}
