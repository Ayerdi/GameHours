using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class SqliteAchievementEvidenceRepositoryTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-evidence",
        Guid.NewGuid().ToString("N"));
    private GameHoursDatabase _database = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new GameHoursDatabase(Path.Combine(_directory, "gamehours.db"));
        await _database.InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Save_PersistsPositiveEvidenceWithAuditableProvenance()
    {
        var gameId = await InsertGameAsync("Evidence game");
        var observedAt = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var repository = new SqliteAchievementEvidenceRepository(_database);

        await repository.SaveAsync(gameId, [Evidence(
            gameId,
            "ACH_STORY",
            provider: "save-provider",
            ruleId: "story.completed",
            ruleVersion: 3,
            sourcePath: @"C:\Saves\slot1.sav",
            fingerprint: "meta:v1:1024:12345",
            observedAt,
            detail: "Persisted quest state is completed.")]);

        var stored = Assert.Single(await repository.GetForGameAsync(gameId));
        Assert.Equal("ACH_STORY", stored.ApiName);
        Assert.Equal(AchievementEvidenceOrigin.SaveGame, stored.Origin);
        Assert.Equal("save-provider", stored.Provider);
        Assert.Equal("story.completed", stored.RuleId);
        Assert.Equal(3, stored.RuleVersion);
        Assert.Equal(@"C:\Saves\slot1.sav", stored.SourcePath);
        Assert.Equal("meta:v1:1024:12345", stored.SourceFingerprint);
        Assert.Equal("Persisted quest state is completed.", stored.Detail);
        Assert.Equal(observedAt, stored.FirstObservedAtUtc);
        Assert.Equal(observedAt, stored.LastObservedAtUtc);
    }

    [Fact]
    public async Task Save_ReobservingSameProofIsIdempotentAndRefreshesLatestAuditData()
    {
        var gameId = await InsertGameAsync("Idempotent game");
        var first = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var earlier = first.AddMinutes(-5);
        var later = first.AddMinutes(5);
        var repository = new SqliteAchievementEvidenceRepository(_database);
        var original = Evidence(gameId, "ACH_ONE", "provider", "rule", 1, @"C:\Saves\slot.sav", "meta:one", first, "first proof");

        await repository.SaveAsync(gameId, [original]);
        await repository.SaveAsync(gameId, [original]);
        await repository.SaveAsync(gameId, [Evidence(gameId, "ACH_ONE", "provider", "rule", 1, @"C:\Saves\slot.sav", "meta:two", later, "newer proof")]);
        await repository.SaveAsync(gameId, [Evidence(gameId, "ACH_ONE", "provider", "rule", 1, @"C:\Saves\slot.sav", "meta:old", earlier, "delayed older proof")]);

        var stored = Assert.Single(await repository.GetForGameAsync(gameId));
        Assert.Equal(earlier, stored.FirstObservedAtUtc);
        Assert.Equal(later, stored.LastObservedAtUtc);
        Assert.Equal("meta:two", stored.SourceFingerprint);
        Assert.Equal("newer proof", stored.Detail);
    }

    [Fact]
    public async Task CompositeIdentitySeparatesAchievementsGamesProvidersRulesAndVersions()
    {
        var firstGame = await InsertGameAsync("First game");
        var secondGame = await InsertGameAsync("Second game");
        var observedAt = DateTimeOffset.Parse("2026-08-31T00:00:00Z");
        var repository = new SqliteAchievementEvidenceRepository(_database);

        await repository.SaveAsync(firstGame, [
            Evidence(firstGame, "ACH_ONE", "provider-a", "rule-a", 1, null, null, observedAt, "one"),
            Evidence(firstGame, "ACH_TWO", "provider-a", "rule-a", 1, null, null, observedAt, "two"),
            Evidence(firstGame, "ACH_ONE", "provider-b", "rule-a", 1, null, null, observedAt, "provider"),
            Evidence(firstGame, "ACH_ONE", "provider-a", "rule-b", 1, null, null, observedAt, "rule"),
            Evidence(firstGame, "ACH_ONE", "provider-a", "rule-a", 2, null, null, observedAt, "version")
        ]);
        await repository.SaveAsync(secondGame, [
            Evidence(secondGame, "ACH_ONE", "provider-a", "rule-a", 1, null, null, observedAt, "game")
        ]);

        var byGame = await repository.GetForGamesAsync([firstGame, secondGame]);
        Assert.Equal(5, byGame[firstGame].Count);
        Assert.Single(byGame[secondGame]);
        Assert.Contains(byGame[firstGame], item => item.Provider == "provider-b");
        Assert.Contains(byGame[firstGame], item => item.RuleId == "rule-b");
        Assert.Contains(byGame[firstGame], item => item.RuleVersion == 2);
    }

    [Fact]
    public async Task GetForGames_BatchesLibrariesBeyondSqliteParameterLimit()
    {
        var repository = new SqliteAchievementEvidenceRepository(_database);
        var gameIds = Enumerable.Range(0, 1_001).Select(_ => Guid.NewGuid()).ToArray();

        var byGame = await repository.GetForGamesAsync(gameIds);

        Assert.Equal(gameIds.Length, byGame.Count);
        Assert.All(byGame.Values, Assert.Empty);
    }

    [Fact]
    public async Task Save_RequiresExistingGameForeignKey()
    {
        var gameId = Guid.NewGuid();
        var repository = new SqliteAchievementEvidenceRepository(_database);
        var proof = Evidence(
            gameId,
            "ACH_ONE",
            "provider",
            "rule",
            1,
            null,
            null,
            DateTimeOffset.Parse("2026-08-31T00:00:00Z"),
            "proof");

        await Assert.ThrowsAsync<SqliteException>(() => repository.SaveAsync(gameId, [proof]));
    }

    private async Task<Guid> InsertGameAsync(string title)
    {
        var gameId = Guid.NewGuid();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES($id, $title, NULL, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.Parse("2026-08-31T00:00:00Z").ToString("O"));
        await command.ExecuteNonQueryAsync();
        return gameId;
    }

    private static ConfirmedAchievementUnlockEvidence Evidence(
        Guid gameId,
        string apiName,
        string provider,
        string ruleId,
        int ruleVersion,
        string? sourcePath,
        string? fingerprint,
        DateTimeOffset observedAt,
        string detail) => new(
            gameId,
            apiName,
            AchievementEvidenceOrigin.SaveGame,
            provider,
            ruleId,
            ruleVersion,
            sourcePath,
            fingerprint,
            observedAt,
            detail);
}
