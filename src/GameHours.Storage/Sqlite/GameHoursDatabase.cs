using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Sqlite;

public sealed class GameHoursDatabase
{
    internal const int CurrentSchemaVersion = 7;
    internal const int ApplicationId = 0x47485253; // "GHRS"
    private readonly string _connectionString;
    public string DatabasePath { get; }

    public GameHoursDatabase(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath)) throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        DatabasePath = Path.GetFullPath(databasePath);
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
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

        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var version = await GetUserVersionAsync(connection, transaction, cancellationToken);
        if (version > CurrentSchemaVersion)
            throw new InvalidOperationException($"Database schema version {version} is newer than supported version {CurrentSchemaVersion}.");

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

        if (version < 3)
        {
            await ExecuteAsync(connection, transaction, MigrationV3, cancellationToken);
            version = 3;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }

        if (version < 4)
        {
            await ExecuteAsync(connection, transaction, MigrationV4, cancellationToken);
            version = 4;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }

        if (version < 5)
        {
            await ExecuteAsync(connection, transaction, MigrationV5, cancellationToken);
            version = 5;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }

        if (version < 6)
        {
            // A restore/import may carry a conservative or stale user_version marker while the
            // v6 column is already present. Treat the schema itself as evidence and keep this
            // additive migration idempotent instead of failing with a duplicate-column error.
            if (!await HasColumnAsync(
                    connection,
                    transaction,
                    "achievement_observation_state",
                    "state_coverage",
                    cancellationToken))
            {
                await ExecuteAsync(connection, transaction, MigrationV6, cancellationToken);
            }

            version = 6;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }

        // Verify this physical shape even when user_version already says v7. This safely repairs
        // an interrupted/development database whose version marker advanced before the additive
        // table was created, without repeating ALTER TABLE operations.
        await EnsureAchievementEvidenceSchemaAsync(connection, transaction, cancellationToken);
        if (version < 7)
        {
            version = 7;
            await SetVersionAsync(connection, transaction, version, cancellationToken);
        }

        await ExecuteAsync(connection, transaction, AchievementCompletionBackfill, cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // SQLite reserves application_id specifically for identifying application-owned files.
        // Existing GameHours databases without the marker remain compatible and are stamped on
        // their next successful initialization.
        await using var identity = connection.CreateCommand();
        identity.CommandText = $"PRAGMA application_id = {ApplicationId};";
        await identity.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<int> GetUserVersionAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(token), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasColumnAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string tableName,
        string columnName,
        CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"PRAGMA table_info([{tableName.Replace("]", "]]", StringComparison.Ordinal)}]);";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task EnsureAchievementEvidenceSchemaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        await ExecuteAsync(connection, transaction, MigrationV7, token);

        var requiredColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "game_id",
            "api_name",
            "origin",
            "provider",
            "rule_id",
            "rule_version",
            "source_path",
            "source_fingerprint",
            "detail",
            "first_observed_at_utc",
            "last_observed_at_utc"
        };
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA table_info(achievement_unlock_evidence);";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            requiredColumns.Remove(reader.GetString(1));
        }

        if (requiredColumns.Count != 0)
        {
            throw new InvalidDataException(
                $"achievement_unlock_evidence has an unsupported physical shape; missing: {string.Join(", ", requiredColumns.Order())}.");
        }
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

    // Candidate admission became deliberately conservative in schema v3. Pending rows are
    // non-authoritative suggestions, so discard suggestions produced by the old broad scanner
    // while preserving every resolved/ignored user decision.
    private const string MigrationV3 = """
        DELETE FROM game_candidates WHERE status = 0;
        """;

    private const string MigrationV4 = """
        CREATE TABLE IF NOT EXISTS session_activity (
            session_id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            focused_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (focused_duration_ms >= 0),
            active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0 AND active_duration_ms <= focused_duration_ms),
            idle_threshold_ms INTEGER NOT NULL CHECK (idle_threshold_ms > 0),
            is_finalized INTEGER NOT NULL DEFAULT 0 CHECK (is_finalized IN (0, 1)),
            updated_at_utc TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS idx_session_activity_game ON session_activity(game_id, updated_at_utc);
        """;

    // v5 makes a disabled AFK filter a first-class persisted state. Rebuild this one small table
    // so zero is valid instead of storing a fake threshold. Existing v4 rows all used a real
    // threshold and therefore migrate with afk_filter_enabled = 1.
    private const string MigrationV5 = """
        ALTER TABLE session_activity RENAME TO session_activity_v4;

        CREATE TABLE session_activity (
            session_id TEXT PRIMARY KEY,
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            focused_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (focused_duration_ms >= 0),
            active_duration_ms INTEGER NOT NULL DEFAULT 0 CHECK (active_duration_ms >= 0 AND active_duration_ms <= focused_duration_ms),
            idle_threshold_ms INTEGER NOT NULL CHECK (idle_threshold_ms >= 0),
            is_finalized INTEGER NOT NULL DEFAULT 0 CHECK (is_finalized IN (0, 1)),
            updated_at_utc TEXT NOT NULL,
            afk_filter_enabled INTEGER NOT NULL DEFAULT 1 CHECK (afk_filter_enabled IN (0, 1)),
            CHECK ((afk_filter_enabled = 0 AND idle_threshold_ms = 0 AND active_duration_ms = 0) OR
                   (afk_filter_enabled = 1 AND idle_threshold_ms > 0))
        );

        INSERT INTO session_activity(
            session_id, game_id, focused_duration_ms, active_duration_ms,
            idle_threshold_ms, is_finalized, updated_at_utc, afk_filter_enabled)
        SELECT session_id, game_id, focused_duration_ms, active_duration_ms,
               idle_threshold_ms, is_finalized, updated_at_utc, 1
        FROM session_activity_v4;

        DROP TABLE session_activity_v4;
        CREATE INDEX idx_session_activity_game ON session_activity(game_id, updated_at_utc);
        """;

    // v6 preserves whether the latest successful achievement read was complete state,
    // positive-unlocks-only, or unknown. Existing rows migrate conservatively as Unknown rather
    // than retroactively claiming that old snapshots proved every locked achievement.
    private const string MigrationV6 = """
        ALTER TABLE achievement_observation_state
        ADD COLUMN state_coverage INTEGER NOT NULL DEFAULT 0
            CHECK (state_coverage IN (0, 1, 2));
        """;

    private const string MigrationV7 = """
        CREATE TABLE IF NOT EXISTS achievement_unlock_evidence (
            game_id TEXT NOT NULL REFERENCES games(id) ON DELETE CASCADE,
            api_name TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(api_name)) > 0),
            origin INTEGER NOT NULL CHECK (origin IN (1, 2, 3)),
            provider TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(provider)) > 0),
            rule_id TEXT NOT NULL COLLATE NOCASE CHECK (length(trim(rule_id)) > 0),
            rule_version INTEGER NOT NULL CHECK (rule_version > 0),
            source_path TEXT NOT NULL DEFAULT '' COLLATE NOCASE,
            source_fingerprint TEXT NULL,
            detail TEXT NOT NULL CHECK (length(trim(detail)) > 0),
            first_observed_at_utc TEXT NOT NULL,
            last_observed_at_utc TEXT NOT NULL,
            PRIMARY KEY (game_id, api_name, provider, rule_id, rule_version, source_path),
            CHECK (last_observed_at_utc >= first_observed_at_utc)
        );
        """;

    private const string AchievementCompletionBackfill = """
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
                         AND COALESCE(final_state.unlocked_at_utc, final_state.first_unlocked_seen_at_utc) = completed.completed_at_utc
                         AND final_state.unlocked_at_utc IS NULL
                   ) THEN 1
                   ELSE 0
               END,
               completed.last_source,
               completed.last_observed_at_utc
        FROM completed_catalogues completed
        WHERE 1 = 1
        ON CONFLICT(game_id) DO NOTHING;
        """;
}
