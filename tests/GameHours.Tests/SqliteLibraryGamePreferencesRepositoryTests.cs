using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteLibraryGamePreferencesRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-library-preferences",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTrip_PreservesFavoriteHiddenAndCompletionStatus()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var game = new TrackedGame(Guid.NewGuid(), "Library Test");
        await games.UpsertAsync(game);
        var repository = new SqliteLibraryGamePreferencesRepository(database);

        await repository.SetAsync(new LibraryGamePreferences(
            game.Id,
            IsFavorite: true,
            IsHidden: true,
            CompletionStatus: LibraryCompletionStatus.Paused));

        var loaded = await repository.GetAsync(game.Id);
        Assert.True(loaded.IsFavorite);
        Assert.True(loaded.IsHidden);
        Assert.Equal(LibraryCompletionStatus.Paused, loaded.CompletionStatus);

        var all = await repository.GetAllAsync();
        Assert.Equal(loaded, all[game.Id]);
    }

    [Fact]
    public async Task SavingDefaultPreferences_RemovesSparseRow()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var game = new TrackedGame(Guid.NewGuid(), "Sparse Test");
        await games.UpsertAsync(game);
        var repository = new SqliteLibraryGamePreferencesRepository(database);

        await repository.SetAsync(new LibraryGamePreferences(game.Id, IsFavorite: true));
        await repository.SetAsync(new LibraryGamePreferences(game.Id));

        var loaded = await repository.GetAsync(game.Id);
        Assert.True(loaded.IsDefault);
        Assert.Empty(await repository.GetAllAsync());
    }

    [Fact]
    public async Task PreferencesFollowGameForeignKeyCascade()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var gameId = Guid.NewGuid();
        var games = new SqliteGameRepository(database);
        await games.UpsertAsync(new TrackedGame(gameId, "Cascade Test"));
        var repository = new SqliteLibraryGamePreferencesRepository(database);
        await repository.SetAsync(new LibraryGamePreferences(gameId, IsFavorite: true));

        await using (var connection = database.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "DELETE FROM games WHERE id = $game_id;";
            command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
            await command.ExecuteNonQueryAsync();
        }

        Assert.Empty(await repository.GetAllAsync());
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
