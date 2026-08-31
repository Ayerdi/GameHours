using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteGameExternalIdentityRepositoryTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-external-identities",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTrip_FindsGameByProviderScopedIdentity()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var game = new TrackedGame(Guid.NewGuid(), "External identity test");
        await new SqliteGameRepository(database).UpsertAsync(game);
        var repository = new SqliteGameExternalIdentityRepository(database);
        var identity = new GameExternalIdentity(GameExternalIdentityProviders.Steam, "3946950");

        await repository.UpsertAsync(game.Id, identity);

        Assert.Equal(game.Id, await repository.FindGameIdAsync(identity));
        Assert.Equal(new[] { identity }, await repository.GetForGameAsync(game.Id));
    }

    [Fact]
    public async Task SameProviderIdentity_CannotSilentlyMoveToAnotherGame()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var first = new TrackedGame(Guid.NewGuid(), "First");
        var second = new TrackedGame(Guid.NewGuid(), "Second");
        await games.UpsertAsync(first);
        await games.UpsertAsync(second);
        var repository = new SqliteGameExternalIdentityRepository(database);
        var identity = new GameExternalIdentity(GameExternalIdentityProviders.Steam, "570");
        await repository.UpsertAsync(first.Id, identity);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => repository.UpsertAsync(second.Id, identity));

        Assert.Contains("already linked", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(first.Id, await repository.FindGameIdAsync(identity));
    }

    [Fact]
    public async Task ProviderNamesAreNormalizedAndProviderNamespacesStayIndependent()
    {
        Directory.CreateDirectory(_directory);
        var database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await database.InitializeAsync();
        var games = new SqliteGameRepository(database);
        var steamGame = new TrackedGame(Guid.NewGuid(), "Steam game");
        var gogGame = new TrackedGame(Guid.NewGuid(), "GOG game");
        await games.UpsertAsync(steamGame);
        await games.UpsertAsync(gogGame);
        var repository = new SqliteGameExternalIdentityRepository(database);

        await repository.UpsertManyAsync(new[]
        {
            (steamGame.Id, new GameExternalIdentity(" STEAM ", "123")),
            (gogGame.Id, new GameExternalIdentity(GameExternalIdentityProviders.Gog, "123"))
        });

        Assert.Equal(steamGame.Id, await repository.FindGameIdAsync(new GameExternalIdentity("steam", "123")));
        Assert.Equal(gogGame.Id, await repository.FindGameIdAsync(new GameExternalIdentity("gog", "123")));
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
