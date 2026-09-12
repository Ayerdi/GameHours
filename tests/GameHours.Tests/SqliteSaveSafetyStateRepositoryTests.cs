using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteSaveSafetyStateRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-save-safety-state",
        Guid.NewGuid().ToString("N"));

    private GameHoursDatabase _database = null!;
    private SqliteSaveSafetyStateRepository _repository = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await _database.InitializeAsync();
        _repository = new SqliteSaveSafetyStateRepository(_database);
    }

    [Fact]
    public async Task Upsert_RoundTripsLatestSuccessfulBackupState()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Fixture Game");
        await new SqliteGameRepository(_database).UpsertAsync(game);
        var attempt = new DateTimeOffset(2026, 9, 12, 19, 0, 0, TimeSpan.Zero);
        var state = new SaveSafetyState(
            game.Id,
            attempt,
            attempt,
            SaveSafetyOperationStatus.Success,
            LastErrorCode: null,
            LastFileCount: 12,
            LastTotalBytes: 3456,
            LastChanged: true,
            UpdatedAtUtc: attempt);

        await _repository.UpsertAsync(state);

        Assert.Equal(state, await _repository.GetAsync(game.Id));
    }

    [Fact]
    public async Task Upsert_FailureCanPreserveEarlierSuccessWithoutClaimingMetrics()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Fixture Game");
        await new SqliteGameRepository(_database).UpsertAsync(game);
        var success = new DateTimeOffset(2026, 9, 12, 18, 0, 0, TimeSpan.Zero);
        var failure = success.AddHours(1);

        await _repository.UpsertAsync(new SaveSafetyState(
            game.Id,
            success,
            success,
            SaveSafetyOperationStatus.Success,
            null,
            2,
            128,
            true,
            success));
        await _repository.UpsertAsync(new SaveSafetyState(
            game.Id,
            failure,
            success,
            SaveSafetyOperationStatus.Failed,
            "BackupFailure",
            null,
            null,
            null,
            failure));

        var stored = Assert.IsType<SaveSafetyState>(await _repository.GetAsync(game.Id));
        Assert.Equal(SaveSafetyOperationStatus.Failed, stored.LastStatus);
        Assert.Equal("BackupFailure", stored.LastErrorCode);
        Assert.Equal(success, stored.LastSuccessAtUtc);
        Assert.Null(stored.LastFileCount);
        Assert.Null(stored.LastTotalBytes);
        Assert.Null(stored.LastChanged);
    }

    [Fact]
    public async Task Upsert_RejectsStateForUnknownGame()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new SaveSafetyState(
            Guid.NewGuid(),
            now,
            now,
            SaveSafetyOperationStatus.Success,
            null,
            1,
            1,
            true,
            now);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.UpsertAsync(state));
    }

    [Fact]
    public async Task DeletingGameCascadesSaveSafetyState()
    {
        var game = new TrackedGame(Guid.NewGuid(), "Fixture Game");
        await new SqliteGameRepository(_database).UpsertAsync(game);
        var now = DateTimeOffset.UtcNow;
        await _repository.UpsertAsync(new SaveSafetyState(
            game.Id,
            now,
            now,
            SaveSafetyOperationStatus.Success,
            null,
            1,
            1,
            true,
            now));

        await using var connection = _database.OpenConnection();
        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM games WHERE id = $game_id;";
        delete.Parameters.AddWithValue("$game_id", game.Id.ToString("D"));
        await delete.ExecuteNonQueryAsync();

        Assert.Null(await _repository.GetAsync(game.Id));
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }
}
