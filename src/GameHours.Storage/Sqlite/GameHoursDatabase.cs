using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class GameHoursDatabase
{
    private const int CurrentSchemaVersion = 2;
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public GameHoursDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared, Pooling = true }.ToString();
    }

    public SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;";
        command.ExecuteNonQuery();
        return connection;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = OpenConnection();
        await using (var wal = connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode = WAL;";
            await wal.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var version = await GetUserVersionAsync(connection, transaction, cancellationToken);
        if (version > CurrentSchemaVersion) throw new InvalidOperationException($"Database schema version {version} is newer than supported version {CurrentSchemaVersion}.");
        if (version == 0)
        {
            await ExecuteAsync(connection, transaction, SchemaV1, cancellationToken);
            version = 1;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }
        if (version < 2)
        {
            await ExecuteAsync(connection, transaction, MigrationV2, cancellationToken);
            version = 2;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static Task SetVersionAsync(SqliteConnection connection, SqliteTransaction transaction, int version, CancellationToken token) =>
        ExecuteAsync(connection, transaction, $"PRAGMA user_version = {version}; UPDATE schema_info SET version = {version};", token);

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(token);
    }

    private const string SchemaV1 = """
        CREATE TABLE IF NOT EXISTS schema_info (version INTEGER NOT NULL);
        INSERT INTO schema_info(version) SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
        CREATE TABLE IF NOT EXISTS tracking_state (singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1), tracking_started_at_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS games (id TEXT PRIMARY KEY, title TEXT NOT NULL, catalog_game_id INTEGER NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS executable_mappings (id TEXT PRIMARY KEY, game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE, executable_path TEXT NOT NULL COLLATE NOCASE, executable_name TEXT NOT NULL COLLATE NOCASE, is_helper INTEGER NOT NULL DEFAULT 0 CHECK (is_helper IN (0, 1)), created_at_utc TEXT NOT NULL, UNIQUE (executable_path));
        CREATE TABLE IF NOT EXISTS sessions (id TEXT PRIMARY KEY, game_id TEXT NOT NULL, started_at_utc TEXT NOT NULL, ended_at_utc TEXT NOT NULL, duration_ms INTEGER NOT NULL CHECK (duration_ms > 0), capture_method INTEGER NOT NULL, confidence INTEGER NOT NULL, end_reason TEXT NULL, created_at_utc TEXT NOT NULL, CHECK (ended_at_utc > started_at_utc));
        CREATE INDEX IF NOT EXISTS idx_sessions_game_time ON sessions(game_id, started_at_utc, ended_at_utc);
        CREATE TABLE IF NOT EXISTS open_sessions (session_id TEXT PRIMARY KEY, game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE, started_at_utc TEXT NOT NULL, last_checkpoint_at_utc TEXT NOT NULL, capture_method INTEGER NOT NULL, created_at_utc TEXT NOT NULL, updated_at_utc TEXT NOT NULL, CHECK (last_checkpoint_at_utc >= started_at_utc));
        CREATE INDEX IF NOT EXISTS idx_open_sessions_game ON open_sessions(game_id);
        CREATE TABLE IF NOT EXISTS historical_evidence (id TEXT PRIMARY KEY, game_id TEXT NOT NULL, source INTEGER NOT NULL, evidence_kind INTEGER NOT NULL, metric INTEGER NOT NULL, confidence INTEGER NOT NULL, period_start_utc TEXT NOT NULL, period_end_utc TEXT NOT NULL, duration_ms INTEGER NOT NULL CHECK (duration_ms > 0), created_at_utc TEXT NOT NULL, CHECK (period_end_utc > period_start_utc));
        CREATE INDEX IF NOT EXISTS idx_historical_evidence_game_time ON historical_evidence(game_id, period_start_utc, period_end_utc);
        CREATE TABLE IF NOT EXISTS achievement_observation_state (game_id TEXT PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE, initialized_at_utc TEXT NOT NULL, last_observed_at_utc TEXT NOT NULL, last_source TEXT NOT NULL, has_complete_catalogue INTEGER NOT NULL DEFAULT 0 CHECK (has_complete_catalogue IN (0, 1)));
        CREATE TABLE IF NOT EXISTS achievement_states (game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE, api_name TEXT NOT NULL COLLATE NOCASE, display_name TEXT NOT NULL, description TEXT NOT NULL DEFAULT '', hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)), is_unlocked INTEGER NOT NULL DEFAULT 0 CHECK (is_unlocked IN (0, 1)), unlocked_at_utc TEXT NULL, source TEXT NOT NULL, first_seen_at_utc TEXT NOT NULL, last_seen_at_utc TEXT NOT NULL, first_unlocked_seen_at_utc TEXT NULL, PRIMARY KEY (game_id, api_name));
        CREATE INDEX IF NOT EXISTS idx_achievement_states_game_unlock ON achievement_states(game_id, is_unlocked, unlocked_at_utc);
        CREATE TABLE IF NOT EXISTS achievement_completion_milestones (game_id TEXT PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE, completed_at_utc TEXT NOT NULL, is_observed_time_fallback INTEGER NOT NULL DEFAULT 0 CHECK (is_observed_time_fallback IN (0, 1)), source TEXT NOT NULL, recorded_at_utc TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS idx_achievement_completion_time ON achievement_completion_milestones(completed_at_utc);
        CREATE TABLE IF NOT EXISTS sync_outbox (id TEXT PRIMARY KEY, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, payload_json TEXT NOT NULL, attempt_count INTEGER NOT NULL DEFAULT 0, next_attempt_at_utc TEXT NOT NULL, created_at_utc TEXT NOT NULL, sent_at_utc TEXT NULL, UNIQUE(entity_type, entity_id));
        """;

    private const string MigrationV2 = """
        CREATE TABLE IF NOT EXISTS game_candidates (
            executable_path TEXT PRIMARY KEY COLLATE NOCASE,
            executable_name TEXT NOT NULL COLLATE NOCASE,
            process_name TEXT NOT NULL,
            suggested_title TEXT NOT NULL,
            confidence REAL NOT NULL,
            method TEXT NOT NULL,
            role INTEGER NOT NULL,
            evidence_json TEXT NOT NULL,
            first_seen_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            observation_count INTEGER NOT NULL DEFAULT 1 CHECK (observation_count > 0),
            status INTEGER NOT NULL DEFAULT 0,
            decision_role INTEGER NULL,
            decision_game_id TEXT NULL,
            resolved_at_utc TEXT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_game_candidates_status_seen ON game_candidates(status, last_seen_at_utc DESC);
        """;
}
