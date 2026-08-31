using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class GameHoursPortableImportTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "gamehours-import-tests", Guid.NewGuid().ToString("N"));

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Import_RoundTripsPortableData_AndRetryIsIdempotent()
    {
        var sourcePath = Path.Combine(_directory, "source.db");
        var targetPath = Path.Combine(_directory, "target.db");
        var exportPath = Path.Combine(_directory, "portable.json");
        var source = new GameHoursDatabase(sourcePath);
        var target = new GameHoursDatabase(targetPath);
        await source.InitializeAsync();
        await target.InitializeAsync();
        var fixture = await SeedSourceAsync(source);
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);
        var importer = new GameHoursPortableImportService(target);

        var preview = await importer.AnalyzeAsync(exportPath);

        Assert.True(preview.CanImport);
        Assert.Equal(0, preview.ConflictCount);
        Assert.Equal(1, preview.NewGameCount);
        Assert.Equal(1, preview.NewSessionCount);
        Assert.Equal(1, preview.NewHistoricalEvidenceCount);
        Assert.Equal(1, preview.NewAchievementCount);
        Assert.Equal(1, preview.NewAchievementEvidenceCount);

        await importer.ImportAsync(exportPath);

        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM games;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM historical_evidence;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM achievement_states;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM achievement_unlock_evidence;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT state_coverage FROM achievement_observation_state;"));
        Assert.Equal(string.Empty, await ScalarTextAsync(target, "SELECT source_path FROM achievement_unlock_evidence;"));
        Assert.Null(await ScalarTextAsync(target, "SELECT source_fingerprint FROM achievement_unlock_evidence;"));
        Assert.Equal("portable-rule", await ScalarTextAsync(target, "SELECT rule_id FROM achievement_unlock_evidence;"));
        Assert.Equal(fixture.Cutover.ToString("O"), await ScalarTextAsync(target, "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id = 1;"));

        var retry = await importer.AnalyzeAsync(exportPath);
        Assert.True(retry.CanImport);
        Assert.Equal(0, retry.NewGameCount);
        Assert.Equal(0, retry.NewSessionCount);
        Assert.Equal(1, retry.DuplicateSessionCount);
        Assert.Equal(0, retry.NewHistoricalEvidenceCount);
        Assert.Equal(1, retry.DuplicateHistoricalEvidenceCount);
        Assert.Equal(0, retry.NewAchievementCount);
        Assert.Equal(0, retry.UpdatedAchievementCount);
        Assert.Equal(0, retry.NewAchievementEvidenceCount);
        Assert.Equal(0, retry.UpdatedAchievementEvidenceCount);

        await importer.ImportAsync(exportPath);
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM historical_evidence;"));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM achievement_unlock_evidence;"));
    }

    [Fact]
    public async Task Import_AcceptsLegacyV1AndKeepsMissingEvidenceAndCoverageConservative()
    {
        var target = new GameHoursDatabase(Path.Combine(_directory, "legacy-v1-target.db"));
        await target.InitializeAsync();
        var gameId = Guid.NewGuid();
        var sourcePath = Path.Combine(_directory, "legacy-v1.json");
        var json = $$"""
            {
              "format_version": 1,
              "exported_at_utc": "2026-08-22T18:00:00Z",
              "source_schema_version": 6,
              "tracking_started_at_utc": null,
              "games": [{"id":"{{gameId:D}}","title":"Legacy game","created_at_utc":"2026-08-22T18:00:00Z","updated_at_utc":"2026-08-22T18:00:00Z"}],
              "sessions": [],
              "historical_evidence": [],
              "achievement_observations": [{"game_id":"{{gameId:D}}","initialized_at_utc":"2026-08-22T18:00:00Z","last_observed_at_utc":"2026-08-22T18:00:00Z","last_source":"legacy","has_complete_catalogue":true}],
              "achievements": [],
              "achievement_completion_milestones": []
            }
            """;
        await File.WriteAllTextAsync(sourcePath, json);

        var importer = new GameHoursPortableImportService(target);
        var preview = await importer.AnalyzeAsync(sourcePath);
        Assert.True(preview.CanImport);
        Assert.Equal(0, preview.SourceAchievementEvidenceCount);
        await importer.ImportAsync(sourcePath);

        Assert.Equal(0L, await ScalarLongAsync(target, "SELECT state_coverage FROM achievement_observation_state;"));
        Assert.Equal(0L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM achievement_unlock_evidence;"));
    }

    [Fact]
    public async Task Import_SameSessionUuidWithDifferentData_IsConflictAndWritesNothing()
    {
        var source = new GameHoursDatabase(Path.Combine(_directory, "uuid-source.db"));
        var target = new GameHoursDatabase(Path.Combine(_directory, "uuid-target.db"));
        await source.InitializeAsync();
        await target.InitializeAsync();
        var fixture = await SeedSourceAsync(source);
        var exportPath = Path.Combine(_directory, "uuid.json");
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);
        await SeedTargetGameAndCutoverAsync(target, fixture.GameId, fixture.Cutover);
        await InsertSessionAsync(target, fixture.SessionId, fixture.GameId, fixture.Cutover.AddHours(3), fixture.Cutover.AddHours(4));
        var importer = new GameHoursPortableImportService(target);

        var preview = await importer.AnalyzeAsync(exportPath);

        Assert.False(preview.CanImport);
        Assert.Contains(preview.Conflicts, item => item.Code == "session_uuid_conflict");
        var exception = await Assert.ThrowsAsync<GameHoursPortableImportConflictException>(() => importer.ImportAsync(exportPath));
        Assert.Contains(exception.Preview.Conflicts, item => item.Code == "session_uuid_conflict");
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM sessions;"));
        Assert.Equal(0L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM historical_evidence;"));
    }

    [Fact]
    public async Task Import_OverlappingMeasuredSession_IsConflictAndWritesNothing()
    {
        var source = new GameHoursDatabase(Path.Combine(_directory, "overlap-source.db"));
        var target = new GameHoursDatabase(Path.Combine(_directory, "overlap-target.db"));
        await source.InitializeAsync();
        await target.InitializeAsync();
        var fixture = await SeedSourceAsync(source);
        var exportPath = Path.Combine(_directory, "overlap.json");
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);
        await SeedTargetGameAndCutoverAsync(target, fixture.GameId, fixture.Cutover);
        await InsertSessionAsync(target, Guid.NewGuid(), fixture.GameId, fixture.Cutover.AddMinutes(50), fixture.Cutover.AddMinutes(80));
        var importer = new GameHoursPortableImportService(target);

        var preview = await importer.AnalyzeAsync(exportPath);

        Assert.False(preview.CanImport);
        Assert.Contains(preview.Conflicts, item => item.Code == "session_overlap");
        await Assert.ThrowsAsync<GameHoursPortableImportConflictException>(() => importer.ImportAsync(exportPath));
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM sessions;"));
    }

    [Fact]
    public async Task Import_SameTitleWithDifferentGameUuid_IsExplicitIdentityConflict()
    {
        var source = new GameHoursDatabase(Path.Combine(_directory, "identity-source.db"));
        var target = new GameHoursDatabase(Path.Combine(_directory, "identity-target.db"));
        await source.InitializeAsync();
        await target.InitializeAsync();
        await SeedSourceAsync(source);
        var exportPath = Path.Combine(_directory, "identity.json");
        await new GameHoursDataPortabilityService(source).ExportPortableJsonAsync(exportPath);

        await using (var connection = target.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc) VALUES($id, 'Imported Test Game', NULL, $now, $now);";
            command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString("D"));
            command.Parameters.AddWithValue("$now", DateTimeOffset.Parse("2026-08-22T18:00:00Z").ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        var preview = await new GameHoursPortableImportService(target).AnalyzeAsync(exportPath);

        Assert.False(preview.CanImport);
        Assert.Contains(preview.Conflicts, item => item.Code == "game_identity_conflict");
        Assert.Equal(1L, await ScalarLongAsync(target, "SELECT COUNT(*) FROM games;"));
    }

    private static async Task<Fixture> SeedSourceAsync(GameHoursDatabase database)
    {
        var gameId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var cutover = DateTimeOffset.Parse("2026-08-22T16:00:00Z");
        var now = DateTimeOffset.Parse("2026-08-22T18:00:00Z");

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracking_state(singleton_id, tracking_started_at_utc) VALUES(1, $cutover);
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES($game, 'Imported Test Game', NULL, $created, $created);
            INSERT INTO sessions(id, game_id, started_at_utc, ended_at_utc, duration_ms, capture_method, confidence, end_reason, created_at_utc)
            VALUES($session, $game, $session_start, $session_end, 1800000, 3, 2, 'process-exit', $created);
            INSERT INTO historical_evidence(id, game_id, source, evidence_kind, metric, confidence, period_start_utc, period_end_utc, duration_ms, created_at_utc)
            VALUES($history, $game, 1, 1, 1, 1, $history_start, $history_end, 900000, $created);
            INSERT INTO achievement_observation_state(game_id, initialized_at_utc, last_observed_at_utc, last_source, has_complete_catalogue, state_coverage)
            VALUES($game, $created, $created, 'gse', 1, 1);
            INSERT INTO achievement_states(game_id, api_name, display_name, description, hidden, is_unlocked, unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc, first_unlocked_seen_at_utc)
            VALUES($game, 'ACH_IMPORT', 'Imported achievement', 'Portable import', 0, 1, $unlock, 'gse', $created, $created, $unlock);
            INSERT INTO achievement_completion_milestones(game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc)
            VALUES($game, $unlock, 0, 'gse', $created);
            INSERT INTO achievement_unlock_evidence(game_id,api_name,origin,provider,rule_id,rule_version,source_path,source_fingerprint,detail,first_observed_at_utc,last_observed_at_utc)
            VALUES($game,'ACH_IMPORT',1,'save-provider','portable-rule',1,'C:\Saves\private.sav','meta:private','Portable proof.',$created,$created);
            """;
        command.Parameters.AddWithValue("$cutover", cutover.ToString("O"));
        command.Parameters.AddWithValue("$game", gameId.ToString("D"));
        command.Parameters.AddWithValue("$session", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$history", evidenceId.ToString("D"));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$session_start", cutover.AddHours(1).ToString("O"));
        command.Parameters.AddWithValue("$session_end", cutover.AddHours(1.5).ToString("O"));
        command.Parameters.AddWithValue("$history_start", cutover.AddDays(-2).ToString("O"));
        command.Parameters.AddWithValue("$history_end", cutover.AddDays(-1).ToString("O"));
        command.Parameters.AddWithValue("$unlock", cutover.AddHours(1.25).ToString("O"));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
        return new Fixture(gameId, sessionId, cutover);
    }

    private static async Task SeedTargetGameAndCutoverAsync(GameHoursDatabase database, Guid gameId, DateTimeOffset cutover)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO tracking_state(singleton_id, tracking_started_at_utc) VALUES(1, $cutover);
            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES($game, 'Imported Test Game', NULL, $now, $now);
            """;
        command.Parameters.AddWithValue("$cutover", cutover.ToString("O"));
        command.Parameters.AddWithValue("$game", gameId.ToString("D"));
        command.Parameters.AddWithValue("$now", DateTimeOffset.Parse("2026-08-22T18:00:00Z").ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task InsertSessionAsync(GameHoursDatabase database, Guid sessionId, Guid gameId, DateTimeOffset start, DateTimeOffset end)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(id, game_id, started_at_utc, ended_at_utc, duration_ms, capture_method, confidence, end_reason, created_at_utc)
            VALUES($id, $game, $start, $end, $duration, 3, 2, 'local', $created);
            """;
        command.Parameters.AddWithValue("$id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$game", gameId.ToString("D"));
        command.Parameters.AddWithValue("$start", start.ToString("O"));
        command.Parameters.AddWithValue("$end", end.ToString("O"));
        command.Parameters.AddWithValue("$duration", checked((long)Math.Round((end - start).TotalMilliseconds)));
        command.Parameters.AddWithValue("$created", DateTimeOffset.Parse("2026-08-22T18:00:00Z").ToString("O"));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ScalarLongAsync(GameHoursDatabase database, string sql)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    private static async Task<string?> ScalarTextAsync(GameHoursDatabase database, string sql)
    {
        await using var connection = database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value);
    }

    private sealed record Fixture(Guid GameId, Guid SessionId, DateTimeOffset Cutover);
}
