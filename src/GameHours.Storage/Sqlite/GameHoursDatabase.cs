using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class GameHoursDatabase
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public GameHoursDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        };
        _connectionString = builder.ToString();
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
        await using var command = connection.CreateCommand();
        command.CommandText = Schema;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string Schema = """
        PRAGMA journal_mode = WAL;
        PRAGMA foreign_keys = ON;

        CREATE TABLE IF NOT EXISTS schema_info (
            version INTEGER NOT NULL
        );

        INSERT INTO schema_info(version)
        SELECT 1
        WHERE NOT EXISTS (SELECT 1 FROM schema_info);

        CREATE TABLE IF NOT EXISTS tracking_state (
            singleton_id INTEGER PRIMARY KEY CHECK (singleton_id = 1),
            tracking_started_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS games (
            id TEXT PRIMARY KEY,
            title TEXT NOT NULL,
            catalog_game_id INTEGER NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS executable_mappings (
            id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            executable_path TEXT NOT NULL COLLATE NOCASE,
            executable_name TEXT NOT NULL COLLATE NOCASE,
            is_helper INTEGER NOT NULL DEFAULT 0 CHECK (is_helper IN (0, 1)),
            created_at_utc TEXT NOT NULL,
            UNIQUE (executable_path)
        );

        CREATE TABLE IF NOT EXISTS sessions (
            id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL,
            started_at_utc TEXT NOT NULL,
            ended_at_utc TEXT NOT NULL,
            duration_ms INTEGER NOT NULL CHECK (duration_ms > 0),
            capture_method INTEGER NOT NULL,
            confidence INTEGER NOT NULL,
            end_reason TEXT NULL,
            created_at_utc TEXT NOT NULL,
            CHECK (ended_at_utc > started_at_utc)
        );

        CREATE INDEX IF NOT EXISTS idx_sessions_game_time
            ON sessions(game_id, started_at_utc, ended_at_utc);

        CREATE TABLE IF NOT EXISTS open_sessions (
            session_id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            started_at_utc TEXT NOT NULL,
            last_checkpoint_at_utc TEXT NOT NULL,
            capture_method INTEGER NOT NULL,
            created_at_utc TEXT NOT NULL,
            updated_at_utc TEXT NOT NULL,
            CHECK (last_checkpoint_at_utc >= started_at_utc)
        );

        CREATE INDEX IF NOT EXISTS idx_open_sessions_game
            ON open_sessions(game_id);

        CREATE TABLE IF NOT EXISTS historical_evidence (
            id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL,
            source INTEGER NOT NULL,
            evidence_kind INTEGER NOT NULL,
            metric INTEGER NOT NULL,
            confidence INTEGER NOT NULL,
            period_start_utc TEXT NOT NULL,
            period_end_utc TEXT NOT NULL,
            duration_ms INTEGER NOT NULL CHECK (duration_ms > 0),
            created_at_utc TEXT NOT NULL,
            CHECK (period_end_utc > period_start_utc)
        );

        CREATE INDEX IF NOT EXISTS idx_historical_evidence_game_time
            ON historical_evidence(game_id, period_start_utc, period_end_utc);

        CREATE TABLE IF NOT EXISTS achievement_observation_state (
            game_id TEXT PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
            initialized_at_utc TEXT NOT NULL,
            last_observed_at_utc TEXT NOT NULL,
            last_source TEXT NOT NULL,
            has_complete_catalogue INTEGER NOT NULL DEFAULT 0 CHECK (has_complete_catalogue IN (0, 1))
        );

        CREATE TABLE IF NOT EXISTS achievement_states (
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            api_name TEXT NOT NULL COLLATE NOCASE,
            display_name TEXT NOT NULL,
            description TEXT NOT NULL DEFAULT '',
            hidden INTEGER NOT NULL DEFAULT 0 CHECK (hidden IN (0, 1)),
            is_unlocked INTEGER NOT NULL DEFAULT 0 CHECK (is_unlocked IN (0, 1)),
            unlocked_at_utc TEXT NULL,
            source TEXT NOT NULL,
            first_seen_at_utc TEXT NOT NULL,
            last_seen_at_utc TEXT NOT NULL,
            first_unlocked_seen_at_utc TEXT NULL,
            PRIMARY KEY (game_id, api_name)
        );

        CREATE INDEX IF NOT EXISTS idx_achievement_states_game_unlock
            ON achievement_states(game_id, is_unlocked, unlocked_at_utc);

        CREATE TABLE IF NOT EXISTS achievement_completion_milestones (
            game_id TEXT PRIMARY KEY REFERENCES games(id) ON DELETE CASCADE,
            completed_at_utc TEXT NOT NULL,
            is_observed_time_fallback INTEGER NOT NULL DEFAULT 0 CHECK (is_observed_time_fallback IN (0, 1)),
            source TEXT NOT NULL,
            recorded_at_utc TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS idx_achievement_completion_time
            ON achievement_completion_milestones(completed_at_utc);

        WITH completed_catalogues AS (
            SELECT observation.game_id,
                   observation.last_source,
                   observation.last_observed_at_utc,
                   MAX(COALESCE(state.unlocked_at_utc, state.first_unlocked_seen_at_utc)) AS completed_at_utc
            FROM achievement_observation_state observation
            JOIN achievement_states state ON state.game_id = observation.game_id
            WHERE observation.has_complete_catalogue = 1
            GROUP BY observation.game_id, observation.last_source, observation.last_observed_at_utc
            HAVING COUNT(*) > 0
               AND SUM(CASE WHEN state.is_unlocked = 1 THEN 1 ELSE 0 END) = COUNT(*)
               AND MAX(COALESCE(state.unlocked_at_utc, state.first_unlocked_seen_at_utc)) IS NOT NULL
        )
        INSERT INTO achievement_completion_milestones(
            game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc)
        SELECT completed.game_id,
               completed.completed_at_utc,
               CASE
                   WHEN EXISTS (
                       SELECT 1
                       FROM achievement_states final_state
                       WHERE final_state.game_id = completed.game_id
                         AND final_state.is_unlocked = 1
                         AND COALESCE(
                             final_state.unlocked_at_utc,
                             final_state.first_unlocked_seen_at_utc) = completed.completed_at_utc
                         AND final_state.unlocked_at_utc IS NULL
                   ) THEN 1
                   ELSE 0
               END,
               completed.last_source,
               completed.last_observed_at_utc
        FROM completed_catalogues completed
        WHERE 1 = 1
        ON CONFLICT(game_id) DO NOTHING;

        CREATE TABLE IF NOT EXISTS sync_outbox (
            id TEXT PRIMARY KEY,
            entity_type TEXT NOT NULL,
            entity_id TEXT NOT NULL,
            payload_json TEXT NOT NULL,
            attempt_count INTEGER NOT NULL DEFAULT 0,
            next_attempt_at_utc TEXT NOT NULL,
            created_at_utc TEXT NOT NULL,
            sent_at_utc TEXT NULL,
            UNIQUE(entity_type, entity_id)
        );
        """;
}
