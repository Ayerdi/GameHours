using System.Text.Json;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;

namespace GameHours.Storage.Sqlite;

public sealed class SqliteGameCandidateRepository : IGameCandidateRepository
{
    private readonly GameHoursDatabase _database;

    public SqliteGameCandidateRepository(GameHoursDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
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

            CREATE INDEX IF NOT EXISTS idx_game_candidates_status_seen
                ON game_candidates(status, last_seen_at_utc DESC);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ObserveAsync(
        GameCandidateObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (observation.Confidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(observation));
        }

        var path = Path.GetFullPath(observation.ExecutablePath);
        var evidenceJson = JsonSerializer.Serialize(observation.Evidence ?? Array.Empty<GameDetectionEvidence>());
        var observedAtUtc = observation.ObservedAtUtc.ToUniversalTime();

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO game_candidates(
                executable_path, executable_name, process_name, suggested_title,
                confidence, method, role, evidence_json,
                first_seen_at_utc, last_seen_at_utc, observation_count, status)
            VALUES(
                $path, $name, $processName, $title,
                $confidence, $method, $role, $evidence,
                $observedAt, $observedAt, 1, 0)
            ON CONFLICT(executable_path) DO UPDATE SET
                executable_name = excluded.executable_name,
                process_name = excluded.process_name,
                suggested_title = excluded.suggested_title,
                confidence = MAX(game_candidates.confidence, excluded.confidence),
                method = excluded.method,
                role = excluded.role,
                evidence_json = excluded.evidence_json,
                last_seen_at_utc = excluded.last_seen_at_utc,
                observation_count = game_candidates.observation_count + 1
            WHERE game_candidates.status = 0;
            """;
        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$name", Path.GetFileName(path));
        command.Parameters.AddWithValue("$processName", observation.ProcessName ?? string.Empty);
        command.Parameters.AddWithValue("$title", string.IsNullOrWhiteSpace(observation.SuggestedTitle)
            ? Path.GetFileNameWithoutExtension(path)
            : observation.SuggestedTitle.Trim());
        command.Parameters.AddWithValue("$confidence", observation.Confidence);
        command.Parameters.AddWithValue("$method", observation.Method ?? string.Empty);
        command.Parameters.AddWithValue("$role", (int)observation.Role);
        command.Parameters.AddWithValue("$evidence", evidenceJson);
        command.Parameters.AddWithValue("$observedAt", SqliteTime.Serialize(observedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GameCandidate>> GetPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<GameCandidate>();
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT executable_path, executable_name, process_name, suggested_title,
                   confidence, method, role, evidence_json,
                   first_seen_at_utc, last_seen_at_utc, observation_count, status,
                   decision_role, decision_game_id, resolved_at_utc
            FROM game_candidates
            WHERE status = 0
            ORDER BY confidence DESC, last_seen_at_utc DESC, executable_name COLLATE NOCASE;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(ReadCandidate(reader));
        }

        return results;
    }

    public async Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM game_candidates WHERE status = 0;";
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task ResolveAsync(
        string executablePath,
        ExecutableRole decisionRole,
        Guid? gameId = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.GetFullPath(executablePath);
        var status = decisionRole == ExecutableRole.Ignored
            ? GameCandidateStatus.Ignored
            : GameCandidateStatus.Resolved;

        await using var connection = _database.OpenConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE game_candidates
            SET status = $status,
                decision_role = $decisionRole,
                decision_game_id = $gameId,
                resolved_at_utc = $resolvedAt
            WHERE executable_path = $path COLLATE NOCASE;
            """;
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$decisionRole", (int)decisionRole);
        command.Parameters.AddWithValue("$gameId", gameId is Guid value ? value.ToString("D") : DBNull.Value);
        command.Parameters.AddWithValue("$resolvedAt", SqliteTime.Serialize(DateTimeOffset.UtcNow));
        command.Parameters.AddWithValue("$path", path);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static GameCandidate ReadCandidate(Microsoft.Data.Sqlite.SqliteDataReader reader)
    {
        var evidence = JsonSerializer.Deserialize<GameDetectionEvidence[]>(reader.GetString(7))
            ?? Array.Empty<GameDetectionEvidence>();
        var decisionRole = reader.IsDBNull(12)
            ? null
            : (ExecutableRole?)reader.GetInt32(12);
        Guid? decisionGameId = reader.IsDBNull(13)
            ? null
            : Guid.Parse(reader.GetString(13));
        DateTimeOffset? resolvedAtUtc = reader.IsDBNull(14)
            ? null
            : SqliteTime.Deserialize(reader.GetString(14));

        return new GameCandidate(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetDouble(4),
            reader.GetString(5),
            (ExecutableRole)reader.GetInt32(6),
            evidence,
            SqliteTime.Deserialize(reader.GetString(8)),
            SqliteTime.Deserialize(reader.GetString(9)),
            reader.GetInt32(10),
            (GameCandidateStatus)reader.GetInt32(11),
            decisionRole,
            decisionGameId,
            resolvedAtUtc);
    }
}
