using System.Globalization;
using System.Text.Json;
using GameHours.Core.Domain;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Portability;

public sealed record GameHoursBackupResult(
    string Path,
    long SizeBytes,
    DateTimeOffset CreatedAtUtc);

public sealed record GameHoursExportResult(
    string Path,
    int FormatVersion,
    int GameCount,
    int SessionCount,
    int HistoricalEvidenceCount,
    int AchievementCount,
    int AchievementEvidenceCount,
    DateTimeOffset ExportedAtUtc);

public sealed class GameHoursDataPortabilityService
{
    public const int CurrentExportFormatVersion = 2;

    private readonly GameHoursDatabase _database;

    public GameHoursDataPortabilityService(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<GameHoursBackupResult> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = NormalizeDestination(destinationPath);
        if (string.Equals(destination, _database.DatabasePath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Backup destination must be different from the live database.", nameof(destinationPath));
        }

        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var source = _database.OpenConnection();
            await using var target = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = temporaryPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
                Pooling = false
            }.ToString());
            await target.OpenAsync(cancellationToken);

            // SQLite's online backup API takes a transactionally consistent snapshot even when
            // the live database is using WAL. Copying only gamehours.db would not provide that guarantee.
            source.BackupDatabase(target);
            await EnsureIntegrityAsync(target, cancellationToken);
            await target.CloseAsync();

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destination, overwrite: true);
            var createdAtUtc = DateTimeOffset.UtcNow;
            return new GameHoursBackupResult(destination, new FileInfo(destination).Length, createdAtUtc);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task<GameHoursExportResult> ExportPortableJsonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var destination = NormalizeDestination(destinationPath);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        var exportedAtUtc = DateTimeOffset.UtcNow;
        var schemaVersion = await ReadSchemaVersionAsync(connection, transaction, cancellationToken);
        var trackingStartedAtUtc = await ReadTrackingStartedAtAsync(connection, transaction, cancellationToken);
        var games = await ReadGamesAsync(connection, transaction, cancellationToken);
        var sessions = await ReadSessionsAsync(connection, transaction, cancellationToken);
        var historical = await ReadHistoricalEvidenceAsync(connection, transaction, cancellationToken);
        var observations = await ReadAchievementObservationsAsync(connection, transaction, cancellationToken);
        var achievements = await ReadAchievementsAsync(connection, transaction, cancellationToken);
        var achievementEvidence = await ReadAchievementEvidenceAsync(connection, transaction, cancellationToken);
        var milestones = await ReadAchievementMilestonesAsync(connection, transaction, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        var export = new PortableExport(
            CurrentExportFormatVersion,
            exportedAtUtc,
            schemaVersion,
            trackingStartedAtUtc,
            games,
            sessions,
            historical,
            observations,
            achievements,
            achievementEvidence,
            milestones);

        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
        var json = JsonSerializer.Serialize(export, options);
        var temporaryPath = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        return new GameHoursExportResult(
            destination,
            CurrentExportFormatVersion,
            games.Count,
            sessions.Count,
            historical.Count,
            achievements.Count,
            achievementEvidence.Count,
            exportedAtUtc);
    }

    private static string NormalizeDestination(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath))
        {
            throw new ArgumentException("Destination path cannot be empty.", nameof(destinationPath));
        }

        return Path.GetFullPath(destinationPath);
    }

    private static async Task EnsureIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"SQLite backup integrity check failed: {result ?? "<no result>"}.");
        }
    }

    private static async Task<int> ReadSchemaVersionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<DateTimeOffset?> ReadTrackingStartedAtAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id = 1;";
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull
            ? null
            : ParseUtc(Convert.ToString(result, CultureInfo.InvariantCulture)!);
    }

    private static async Task<IReadOnlyList<PortableGame>> ReadGamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, title, created_at_utc, updated_at_utc FROM games ORDER BY title COLLATE NOCASE, id;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableGame>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableGame(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                ParseUtc(reader.GetString(2)),
                ParseUtc(reader.GetString(3))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableSession>> ReadSessionsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, game_id, started_at_utc, ended_at_utc, duration_ms,
                   capture_method, confidence, end_reason
            FROM sessions
            ORDER BY started_at_utc, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableSession>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableSession(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                ParseUtc(reader.GetString(2)),
                ParseUtc(reader.GetString(3)),
                reader.GetInt64(4),
                EnumWireName<CaptureMethod>(reader.GetInt32(5)),
                EnumWireName<Confidence>(reader.GetInt32(6)),
                reader.IsDBNull(7) ? null : reader.GetString(7)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableHistoricalEvidence>> ReadHistoricalEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id, game_id, source, evidence_kind, metric, confidence,
                   period_start_utc, period_end_utc, duration_ms
            FROM historical_evidence
            ORDER BY period_start_utc, id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableHistoricalEvidence>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableHistoricalEvidence(
                Guid.Parse(reader.GetString(0)),
                Guid.Parse(reader.GetString(1)),
                EnumWireName<HistoricalSource>(reader.GetInt32(2)),
                EnumWireName<EvidenceKind>(reader.GetInt32(3)),
                EnumWireName<PlaytimeMetric>(reader.GetInt32(4)),
                EnumWireName<Confidence>(reader.GetInt32(5)),
                ParseUtc(reader.GetString(6)),
                ParseUtc(reader.GetString(7)),
                reader.GetInt64(8)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableAchievementObservation>> ReadAchievementObservationsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT game_id, initialized_at_utc, last_observed_at_utc, last_source,
                   has_complete_catalogue, state_coverage
            FROM achievement_observation_state
            ORDER BY game_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableAchievementObservation>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableAchievementObservation(
                Guid.Parse(reader.GetString(0)),
                ParseUtc(reader.GetString(1)),
                ParseUtc(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                EnumWireName<AchievementStateEvidenceCoverage>(reader.GetInt32(5))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableAchievement>> ReadAchievementsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT game_id, api_name, display_name, description, hidden, is_unlocked,
                   unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc,
                   first_unlocked_seen_at_utc
            FROM achievement_states
            ORDER BY game_id, api_name COLLATE NOCASE;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableAchievement>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableAchievement(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetInt64(4) != 0,
                reader.GetInt64(5) != 0,
                reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)),
                reader.GetString(7),
                ParseUtc(reader.GetString(8)),
                ParseUtc(reader.GetString(9)),
                reader.IsDBNull(10) ? null : ParseUtc(reader.GetString(10))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableAchievementMilestone>> ReadAchievementMilestonesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc
            FROM achievement_completion_milestones
            ORDER BY completed_at_utc, game_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<PortableAchievementMilestone>();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PortableAchievementMilestone(
                Guid.Parse(reader.GetString(0)),
                ParseUtc(reader.GetString(1)),
                reader.GetInt64(2) != 0,
                reader.GetString(3),
                ParseUtc(reader.GetString(4))));
        }
        return result;
    }

    private static async Task<IReadOnlyList<PortableAchievementEvidence>> ReadAchievementEvidenceAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT game_id, api_name, origin, provider, rule_id, rule_version,
                   detail, first_observed_at_utc, last_observed_at_utc
            FROM achievement_unlock_evidence
            ORDER BY game_id, api_name COLLATE NOCASE, provider COLLATE NOCASE,
                     rule_id COLLATE NOCASE, rule_version, last_observed_at_utc;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var evidence = new Dictionary<string, PortableAchievementEvidence>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(cancellationToken))
        {
            var item = new PortableAchievementEvidence(
                Guid.Parse(reader.GetString(0)),
                reader.GetString(1),
                EnumWireName<AchievementEvidenceOrigin>(reader.GetInt32(2)),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetInt32(5),
                reader.GetString(6),
                ParseUtc(reader.GetString(7)),
                ParseUtc(reader.GetString(8)));
            var key = $"{item.GameId:D}|{item.ApiName}|{item.Provider}|{item.RuleId}|{item.RuleVersion.ToString(CultureInfo.InvariantCulture)}";
            if (evidence.TryGetValue(key, out var existing))
            {
                evidence[key] = item with
                {
                    FirstObservedAtUtc = existing.FirstObservedAtUtc <= item.FirstObservedAtUtc
                        ? existing.FirstObservedAtUtc
                        : item.FirstObservedAtUtc
                };
            }
            else
            {
                evidence.Add(key, item);
            }
        }
        return evidence.Values.ToArray();
    }

    private static DateTimeOffset ParseUtc(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static string EnumWireName<TEnum>(int value) where TEnum : struct, Enum
    {
        var name = Enum.GetName(typeof(TEnum), value)
            ?? throw new InvalidDataException($"Unknown {typeof(TEnum).Name} value {value} in database.");
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var character = name[index];
            if (char.IsUpper(character) && index > 0)
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(character));
        }
        return builder.ToString();
    }

    private sealed record PortableExport(
        int FormatVersion,
        DateTimeOffset ExportedAtUtc,
        int SourceSchemaVersion,
        DateTimeOffset? TrackingStartedAtUtc,
        IReadOnlyList<PortableGame> Games,
        IReadOnlyList<PortableSession> Sessions,
        IReadOnlyList<PortableHistoricalEvidence> HistoricalEvidence,
        IReadOnlyList<PortableAchievementObservation> AchievementObservations,
        IReadOnlyList<PortableAchievement> Achievements,
        IReadOnlyList<PortableAchievementEvidence> AchievementUnlockEvidence,
        IReadOnlyList<PortableAchievementMilestone> AchievementCompletionMilestones);

    private sealed record PortableGame(Guid Id, string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);

    private sealed record PortableSession(
        Guid Id,
        Guid GameId,
        DateTimeOffset StartedAtUtc,
        DateTimeOffset EndedAtUtc,
        long DurationMilliseconds,
        string CaptureMethod,
        string Confidence,
        string? EndReason);

    private sealed record PortableHistoricalEvidence(
        Guid Id,
        Guid GameId,
        string Source,
        string EvidenceKind,
        string Metric,
        string Confidence,
        DateTimeOffset PeriodStartUtc,
        DateTimeOffset PeriodEndUtc,
        long DurationMilliseconds);

    private sealed record PortableAchievementObservation(
        Guid GameId,
        DateTimeOffset InitializedAtUtc,
        DateTimeOffset LastObservedAtUtc,
        string LastSource,
        bool HasCompleteCatalogue,
        string StateCoverage);

    private sealed record PortableAchievement(
        Guid GameId,
        string ApiName,
        string DisplayName,
        string Description,
        bool Hidden,
        bool IsUnlocked,
        DateTimeOffset? UnlockedAtUtc,
        string Source,
        DateTimeOffset FirstSeenAtUtc,
        DateTimeOffset LastSeenAtUtc,
        DateTimeOffset? FirstUnlockedSeenAtUtc);

    private sealed record PortableAchievementEvidence(
        Guid GameId,
        string ApiName,
        string Origin,
        string Provider,
        string RuleId,
        int RuleVersion,
        string Detail,
        DateTimeOffset FirstObservedAtUtc,
        DateTimeOffset LastObservedAtUtc);

    private sealed record PortableAchievementMilestone(
        Guid GameId,
        DateTimeOffset CompletedAtUtc,
        bool IsObservedTimeFallback,
        string Source,
        DateTimeOffset RecordedAtUtc);
}
