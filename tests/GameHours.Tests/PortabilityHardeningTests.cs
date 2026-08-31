using System.Text.Json.Nodes;
using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class PortabilityHardeningTests : IAsyncLifetime
{
    private const int GameHoursApplicationId = 0x47485253; // "GHRS"
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gamehours-portability-hardening", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Initialize_StampsGameHoursApplicationId()
    {
        var database = new GameHoursDatabase(Path.Combine(_directory, "identity.db"));
        await database.InitializeAsync();

        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA application_id;";

        Assert.Equal(GameHoursApplicationId, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task Restore_RejectsUnrelatedValidSqliteBeforeSafetyBackupOrLiveWrite()
    {
        var livePath = Path.Combine(_directory, "foreign-live.db");
        var foreignPath = Path.Combine(_directory, "foreign-source.db");
        var safetyPath = Path.Combine(_directory, "foreign-safety.db");
        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();
        await InsertGameAsync(live, Guid.NewGuid(), "Keep me");

        await using (var foreign = new SqliteConnection($"Data Source={foreignPath}"))
        {
            await foreign.OpenAsync();
            await using var command = foreign.CreateCommand();
            command.CommandText = "CREATE TABLE notes(id INTEGER PRIMARY KEY, body TEXT NOT NULL); INSERT INTO notes(body) VALUES('not gamehours');";
            await command.ExecuteNonQueryAsync();
        }

        var service = new GameHoursDataRestoreService(live);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreBackupAsync(foreignPath, safetyPath));

        Assert.Contains("GameHours", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(safetyPath));
        Assert.Equal(new[] { "Keep me" }, await ReadGameTitlesAsync(livePath));
    }

    [Fact]
    public async Task Restore_RejectsInconsistentSchemaMarkersBeforeSafetyBackupOrLiveWrite()
    {
        var livePath = Path.Combine(_directory, "mismatch-live.db");
        var sourcePath = Path.Combine(_directory, "mismatch-source.db");
        var safetyPath = Path.Combine(_directory, "mismatch-safety.db");
        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();
        await InsertGameAsync(live, Guid.NewGuid(), "Keep live");

        var source = new GameHoursDatabase(sourcePath);
        await source.InitializeAsync();
        await using (var connection = source.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA user_version = 4;";
            await command.ExecuteNonQueryAsync();
        }

        var service = new GameHoursDataRestoreService(live);
        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.RestoreBackupAsync(sourcePath, safetyPath));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(safetyPath));
        Assert.Equal(new[] { "Keep live" }, await ReadGameTitlesAsync(livePath));
    }

    [Fact]
    public async Task Restore_AcceptsUnmarkedGameHoursBackupForBackwardCompatibility()
    {
        var livePath = Path.Combine(_directory, "legacy-live.db");
        var sourcePath = Path.Combine(_directory, "legacy-source.db");
        var safetyPath = Path.Combine(_directory, "legacy-safety.db");
        var live = new GameHoursDatabase(livePath);
        await live.InitializeAsync();
        await InsertGameAsync(live, Guid.NewGuid(), "Old live");

        var source = new GameHoursDatabase(sourcePath);
        await source.InitializeAsync();
        await InsertGameAsync(source, Guid.NewGuid(), "Legacy backup game");
        await using (var connection = source.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA application_id = 0;";
            await command.ExecuteNonQueryAsync();
        }

        await new GameHoursDataRestoreService(live).RestoreBackupAsync(sourcePath, safetyPath);

        Assert.Equal(new[] { "Legacy backup game" }, await ReadGameTitlesAsync(livePath));
        Assert.True(File.Exists(safetyPath));
    }

    [Fact]
    public async Task Import_RevalidatesAfterPreviewAndRejectsNewIdentityConflict()
    {
        var source = new GameHoursDatabase(Path.Combine(_directory, "revalidate-source.db"));
        var target = new GameHoursDatabase(Path.Combine(_directory, "revalidate-target.db"));
        await source.InitializeAsync();
        await target.InitializeAsync();
        var importedGameId = Guid.NewGuid();
        await InsertGameAsync(source, importedGameId, "Revalidated Game");
        var exportPath = Path.Combine(_directory, "revalidate.json");
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);
        var importer = new GameHoursPortableImportService(target);

        var preview = await importer.AnalyzeAsync(exportPath);
        Assert.True(preview.CanImport);
        Assert.Equal(1, preview.NewGameCount);

        await InsertGameAsync(target, Guid.NewGuid(), "Revalidated Game");

        var exception = await Assert.ThrowsAsync<GameHoursPortableImportConflictException>(() =>
            importer.ImportAsync(exportPath));

        Assert.Contains(exception.Preview.Conflicts, conflict => conflict.Code == "game_identity_conflict");
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM games;"));
        Assert.Equal(0L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM games WHERE id = $id;", ("$id", importedGameId.ToString("D"))));
    }

    [Fact]
    public async Task Import_UnsupportedFormatBlocksEntireFileWithoutWrites()
    {
        var source = new GameHoursDatabase(Path.Combine(_directory, "format-source.db"));
        var target = new GameHoursDatabase(Path.Combine(_directory, "format-target.db"));
        await source.InitializeAsync();
        await target.InitializeAsync();
        await InsertGameAsync(source, Guid.NewGuid(), "Future format game");
        var exportPath = Path.Combine(_directory, "future-format.json");
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);

        var document = JsonNode.Parse(await File.ReadAllTextAsync(exportPath))!.AsObject();
        document["format_version"] = GameHoursDataPortabilityService.CurrentExportFormatVersion + 1;
        await File.WriteAllTextAsync(exportPath, document.ToJsonString());

        var importer = new GameHoursPortableImportService(target);
        var preview = await importer.AnalyzeAsync(exportPath);
        Assert.False(preview.CanImport);
        Assert.Contains(preview.Conflicts, conflict => conflict.Code == "unsupported_format_version");

        await Assert.ThrowsAsync<GameHoursPortableImportConflictException>(() => importer.ImportAsync(exportPath));
        Assert.Equal(0L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM games;"));
    }

    private static async Task InsertGameAsync(GameHoursDatabase database, Guid gameId, string title)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES($id, $title, NULL, $now, $now);
            """;
        command.Parameters.AddWithValue("$id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<IReadOnlyList<string>> ReadGameTitlesAsync(string databasePath)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT title FROM games ORDER BY title COLLATE NOCASE;";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<long> ScalarLongAsync(
        GameHoursDatabase database,
        string sql,
        params (string Name, object Value)[] parameters)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }
}
