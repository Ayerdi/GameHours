using System.Text.Json;
using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Tests;

public sealed class GameHoursDataPortabilityTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-tests",
        Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "gamehours.db");

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Backup_UsesConsistentSqliteSnapshot_AndCanBeOpenedIndependently()
    {
        var database = new GameHoursDatabase(DatabasePath);
        await database.InitializeAsync();
        var fixture = await SeedPortableFixtureAsync(database);
        var service = new GameHoursDataPortabilityService(database);
        var backupPath = Path.Combine(_directory, "backups", "gamehours-backup.db");

        var result = await service.CreateBackupAsync(backupPath);

        Assert.Equal(Path.GetFullPath(backupPath), result.Path);
        Assert.True(result.SizeBytes > 0);
        Assert.True(File.Exists(backupPath));

        await using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = backupPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await backup.OpenAsync();

        await using var integrity = backup.CreateCommand();
        integrity.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", Convert.ToString(await integrity.ExecuteScalarAsync()));

        await using var game = backup.CreateCommand();
        game.CommandText = "SELECT title FROM games WHERE id = $id;";
        game.Parameters.AddWithValue("$id", fixture.GameId.ToString("D"));
        Assert.Equal("Portable Test Game", Convert.ToString(await game.ExecuteScalarAsync()));

        await using var session = backup.CreateCommand();
        session.CommandText = "SELECT COUNT(*) FROM sessions WHERE id = $id;";
        session.Parameters.AddWithValue("$id", fixture.SessionId.ToString("D"));
        Assert.Equal(1L, Convert.ToInt64(await session.ExecuteScalarAsync()));

        await using var activity = backup.CreateCommand();
        activity.CommandText = "SELECT active_duration_ms FROM session_activity WHERE session_id = $id;";
        activity.Parameters.AddWithValue("$id", fixture.SessionId.ToString("D"));
        Assert.Equal(600000L, Convert.ToInt64(await activity.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task PortableExport_IncludesDomainHistory_AndExcludesMachineSpecificState()
    {
        var database = new GameHoursDatabase(DatabasePath);
        await database.InitializeAsync();
        var fixture = await SeedPortableFixtureAsync(database);
        var service = new GameHoursDataPortabilityService(database);
        var exportPath = Path.Combine(_directory, "exports", "gamehours-export.json");

        await using var schemaConnection = database.OpenConnection();
        await using var schemaCommand = schemaConnection.CreateCommand();
        schemaCommand.CommandText = "PRAGMA user_version;";
        var expectedSchemaVersion = Convert.ToInt32(await schemaCommand.ExecuteScalarAsync());

        var result = await service.ExportPortableJsonAsync(exportPath);

        Assert.Equal(GameHoursDataPortabilityService.CurrentExportFormatVersion, result.FormatVersion);
        Assert.Equal(1, result.GameCount);
        Assert.Equal(1, result.SessionCount);
        Assert.Equal(1, result.HistoricalEvidenceCount);
        Assert.Equal(1, result.AchievementCount);

        var json = await File.ReadAllTextAsync(exportPath);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.Equal(1, root.GetProperty("format_version").GetInt32());
        Assert.Equal(expectedSchemaVersion, root.GetProperty("source_schema_version").GetInt32());
        Assert.Equal(fixture.GameId, root.GetProperty("games")[0].GetProperty("id").GetGuid());
        Assert.Equal("Portable Test Game", root.GetProperty("games")[0].GetProperty("title").GetString());
        Assert.Equal(fixture.SessionId, root.GetProperty("sessions")[0].GetProperty("id").GetGuid());
        Assert.Equal(fixture.GameId, root.GetProperty("sessions")[0].GetProperty("game_id").GetGuid());
        Assert.Equal("reconciliation", root.GetProperty("sessions")[0].GetProperty("capture_method").GetString());
        Assert.Equal("high", root.GetProperty("sessions")[0].GetProperty("confidence").GetString());
        Assert.Equal("srum", root.GetProperty("historical_evidence")[0].GetProperty("source").GetString());
        Assert.Equal("baseline", root.GetProperty("historical_evidence")[0].GetProperty("evidence_kind").GetString());
        Assert.Equal("estimated", root.GetProperty("historical_evidence")[0].GetProperty("confidence").GetString());
        Assert.Equal("ACH_TEST", root.GetProperty("achievements")[0].GetProperty("api_name").GetString());

        Assert.DoesNotContain("C:\\Games\\private\\game.exe", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("executable_mappings", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("game_candidates", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sync_outbox", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("catalog_game_id", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("session_activity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("focused_duration", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("active_duration", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Backup_RefusesToOverwriteLiveDatabase()
    {
        var database = new GameHoursDatabase(DatabasePath);
        await database.InitializeAsync();
        var service = new GameHoursDataPortabilityService(database);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.CreateBackupAsync(DatabasePath));

        Assert.Contains("different from the live database", exception.Message, StringComparison.Ordinal);
    }

    private static async Task<FixtureIds> SeedPortableFixtureAsync(GameHoursDatabase database)
    {
        var gameId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var evidenceId = Guid.NewGuid();
        var now = DateTimeOffset.Parse("2026-08-22T18:00:00Z");

        await using var connection = database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracking_state(singleton_id, tracking_started_at_utc)
            VALUES (1, $cutover);

            INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc)
            VALUES ($game_id, 'Portable Test Game', 381, $created, $created);

            INSERT INTO sessions(
                id, game_id, started_at_utc, ended_at_utc, duration_ms,
                capture_method, confidence, end_reason, created_at_utc)
            VALUES ($session_id, $game_id, $session_start, $session_end, 1800000, 3, 2, 'process-exit', $created);

            INSERT INTO session_activity(
                session_id, game_id, focused_duration_ms, active_duration_ms,
                idle_threshold_ms, is_finalized, updated_at_utc)
            VALUES ($session_id, $game_id, 900000, 600000, 300000, 1, $created);

            INSERT INTO historical_evidence(
                id, game_id, source, evidence_kind, metric, confidence,
                period_start_utc, period_end_utc, duration_ms, created_at_utc)
            VALUES ($evidence_id, $game_id, 1, 1, 1, 1, $history_start, $history_end, 900000, $created);

            INSERT INTO achievement_observation_state(
                game_id, initialized_at_utc, last_observed_at_utc, last_source, has_complete_catalogue)
            VALUES ($game_id, $created, $created, 'gse', 1);

            INSERT INTO achievement_states(
                game_id, api_name, display_name, description, hidden, is_unlocked,
                unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc, first_unlocked_seen_at_utc)
            VALUES ($game_id, 'ACH_TEST', 'Portable achievement', 'Test description', 0, 1,
                    $unlocked, 'gse', $created, $created, $unlocked);

            INSERT INTO achievement_completion_milestones(
                game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc)
            VALUES ($game_id, $unlocked, 0, 'gse', $created);

            INSERT INTO executable_mappings(
                id, game_id, executable_path, executable_name, is_helper, created_at_utc)
            VALUES ($mapping_id, $game_id, 'C:\Games\private\game.exe', 'game.exe', 0, $created);

            INSERT INTO game_candidates(
                executable_path, executable_name, process_name, suggested_title,
                confidence, method, role, evidence_json, first_seen_at_utc, last_seen_at_utc,
                observation_count, status)
            VALUES ('C:\Games\private\candidate.exe', 'candidate.exe', 'candidate', 'Private Candidate',
                    0.5, 'graphics', 0, '{}', $created, $created, 1, 0);

            INSERT INTO sync_outbox(
                id, entity_type, entity_id, payload_json, attempt_count,
                next_attempt_at_utc, created_at_utc, sent_at_utc)
            VALUES ($outbox_id, 'session', $session_id, '{"private":"transport"}', 0, $created, $created, NULL);
            """;
        command.Parameters.AddWithValue("$cutover", now.AddHours(-2).ToString("O"));
        command.Parameters.AddWithValue("$game_id", gameId.ToString("D"));
        command.Parameters.AddWithValue("$session_id", sessionId.ToString("D"));
        command.Parameters.AddWithValue("$evidence_id", evidenceId.ToString("D"));
        command.Parameters.AddWithValue("$mapping_id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$outbox_id", Guid.NewGuid().ToString("D"));
        command.Parameters.AddWithValue("$created", now.ToString("O"));
        command.Parameters.AddWithValue("$session_start", now.AddMinutes(-30).ToString("O"));
        command.Parameters.AddWithValue("$session_end", now.ToString("O"));
        command.Parameters.AddWithValue("$history_start", now.AddDays(-2).ToString("O"));
        command.Parameters.AddWithValue("$history_end", now.AddDays(-1).ToString("O"));
        command.Parameters.AddWithValue("$unlocked", now.AddMinutes(-10).ToString("O"));
        await command.ExecuteNonQueryAsync();
        await transaction.CommitAsync();

        return new FixtureIds(gameId, sessionId);
    }

    private sealed record FixtureIds(Guid GameId, Guid SessionId);
}
