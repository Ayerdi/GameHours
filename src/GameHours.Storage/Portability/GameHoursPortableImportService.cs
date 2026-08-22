using System.Globalization;
using System.Text.Json;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Portability;

public sealed record GameHoursPortableImportConflict(
    string Code,
    string EntityType,
    string EntityId,
    string Message);

public sealed record GameHoursPortableImportPreview(
    string SourcePath,
    int FormatVersion,
    int SourceGameCount,
    int SourceSessionCount,
    int SourceHistoricalEvidenceCount,
    int SourceAchievementCount,
    int NewGameCount,
    int UpdatedGameCount,
    int NewSessionCount,
    int DuplicateSessionCount,
    int NewHistoricalEvidenceCount,
    int DuplicateHistoricalEvidenceCount,
    int NewAchievementCount,
    int UpdatedAchievementCount,
    int ConflictCount,
    IReadOnlyList<GameHoursPortableImportConflict> Conflicts)
{
    public bool CanImport => ConflictCount == 0;
}

public sealed record GameHoursPortableImportResult(
    GameHoursPortableImportPreview Preview,
    DateTimeOffset ImportedAtUtc);

public sealed class GameHoursPortableImportConflictException : InvalidDataException
{
    public GameHoursPortableImportPreview Preview { get; }

    public GameHoursPortableImportConflictException(GameHoursPortableImportPreview preview)
        : base($"Portable import contains {preview.ConflictCount} conflict(s) and was not applied.")
    {
        Preview = preview;
    }
}

/// <summary>
/// Imports GameHours portable JSON into an existing database without guessing identity or timeline
/// conflicts. Analysis and apply use the same validation path; ImportAsync revalidates everything
/// inside the write transaction before changing any durable data.
/// </summary>
public sealed class GameHoursPortableImportService
{
    private readonly GameHoursDatabase _database;

    public GameHoursPortableImportService(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<GameHoursPortableImportPreview> AnalyzeAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeSource(sourcePath);
        var document = await ReadDocumentAsync(source, cancellationToken);
        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await BuildPlanAsync(source, document, connection, transaction, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return plan.Preview;
    }

    public async Task<GameHoursPortableImportResult> ImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default)
    {
        var source = NormalizeSource(sourcePath);
        var document = await ReadDocumentAsync(source, cancellationToken);
        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await BuildPlanAsync(source, document, connection, transaction, cancellationToken);
        if (!plan.Preview.CanImport)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new GameHoursPortableImportConflictException(plan.Preview);
        }

        await ApplyPlanAsync(plan, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new GameHoursPortableImportResult(plan.Preview, DateTimeOffset.UtcNow);
    }

    private static string NormalizeSource(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Import source path cannot be empty.", nameof(sourcePath));
        }

        var source = Path.GetFullPath(sourcePath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("The selected GameHours portable export does not exist.", source);
        }
        return source;
    }

    private static async Task<PortableImportDocument> ReadDocumentAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            var document = await JsonSerializer.DeserializeAsync<PortableImportDocument>(
                stream,
                JsonOptions,
                cancellationToken);
            return document ?? throw new InvalidDataException("Portable export is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The selected file is not valid GameHours portable JSON.", exception);
        }
    }

    private static async Task<ImportPlan> BuildPlanAsync(
        string sourcePath,
        PortableImportDocument document,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        var conflicts = new List<GameHoursPortableImportConflict>();
        var games = document.Games ?? Array.Empty<PortableGame>();
        var sessions = document.Sessions ?? Array.Empty<PortableSession>();
        var historical = document.HistoricalEvidence ?? Array.Empty<PortableHistoricalEvidence>();
        var observations = document.AchievementObservations ?? Array.Empty<PortableAchievementObservation>();
        var achievements = document.Achievements ?? Array.Empty<PortableAchievement>();
        var milestones = document.AchievementCompletionMilestones ?? Array.Empty<PortableAchievementMilestone>();

        if (document.FormatVersion != GameHoursDataPortabilityService.CurrentExportFormatVersion)
        {
            conflicts.Add(new(
                "unsupported_format_version",
                "document",
                document.FormatVersion.ToString(CultureInfo.InvariantCulture),
                $"Portable format v{document.FormatVersion} is not supported; this build supports v{GameHoursDataPortabilityService.CurrentExportFormatVersion}."));
        }

        var localGames = await ReadLocalGamesAsync(connection, transaction, cancellationToken);
        var localSessions = await ReadLocalSessionsAsync(connection, transaction, cancellationToken);
        var localHistorical = await ReadLocalHistoricalAsync(connection, transaction, cancellationToken);
        var localAchievements = await ReadLocalAchievementsAsync(connection, transaction, cancellationToken);
        var localObservations = await ReadLocalObservationsAsync(connection, transaction, cancellationToken);
        var localMilestones = await ReadLocalMilestonesAsync(connection, transaction, cancellationToken);
        var localCutover = await ReadLocalCutoverAsync(connection, transaction, cancellationToken);

        var gameInserts = new List<PortableGame>();
        var gameUpdates = new List<PortableGame>();
        var validGameIds = new HashSet<Guid>(localGames.Keys);
        var fileGameIds = new HashSet<Guid>();
        var fileTitles = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityId = game.Id.ToString("D");
            if (game.Id == Guid.Empty || string.IsNullOrWhiteSpace(game.Title))
            {
                conflicts.Add(new("invalid_game", "game", entityId, "Game id and title must be present."));
                continue;
            }
            if (!fileGameIds.Add(game.Id))
            {
                conflicts.Add(new("duplicate_game_id_in_file", "game", entityId, "The portable file contains the same game UUID more than once."));
                continue;
            }
            var title = game.Title.Trim();
            if (fileTitles.TryGetValue(title, out var otherFileId) && otherFileId != game.Id)
            {
                conflicts.Add(new("ambiguous_game_title_in_file", "game", entityId, $"Title '{title}' is assigned to more than one game UUID in the portable file."));
                continue;
            }
            fileTitles[title] = game.Id;

            var sameTitleLocal = localGames.Values.FirstOrDefault(item =>
                item.Id != game.Id && string.Equals(item.Title, title, StringComparison.OrdinalIgnoreCase));
            if (sameTitleLocal is not null)
            {
                conflicts.Add(new(
                    "game_identity_conflict",
                    "game",
                    entityId,
                    $"'{title}' already exists locally with a different GameHours UUID ({sameTitleLocal.Id:D}). Import v1 will not guess which identity is canonical."));
                continue;
            }

            validGameIds.Add(game.Id);
            if (!localGames.TryGetValue(game.Id, out var localGame))
            {
                gameInserts.Add(game with { Title = title });
            }
            else if (!string.Equals(localGame.Title, title, StringComparison.Ordinal) && game.UpdatedAtUtc > localGame.UpdatedAtUtc)
            {
                gameUpdates.Add(game with { Title = title });
            }
        }

        var effectiveCutover = localCutover ?? document.TrackingStartedAtUtc?.ToUniversalTime();
        if ((sessions.Length > 0 || historical.Length > 0) && effectiveCutover is null)
        {
            conflicts.Add(new(
                "missing_tracking_cutover",
                "timeline",
                "tracking_started_at_utc",
                "Measured sessions or historical evidence cannot be imported without a tracking_started_at_utc boundary."));
        }

        var sessionInserts = new List<NormalizedSession>();
        var sessionDuplicates = 0;
        var fileSessionIds = new HashSet<Guid>();
        foreach (var item in sessions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityId = item.Id.ToString("D");
            if (!fileSessionIds.Add(item.Id))
            {
                conflicts.Add(new("duplicate_session_id_in_file", "session", entityId, "The portable file contains the same session UUID more than once."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId))
            {
                conflicts.Add(new("unknown_game", "session", entityId, $"Session references unknown game UUID {item.GameId:D}."));
                continue;
            }
            if (!TryNormalizeSession(item, out var normalized, out var error))
            {
                conflicts.Add(new("invalid_session", "session", entityId, error!));
                continue;
            }
            if (effectiveCutover is { } cutover)
            {
                try { PlaytimeTimelineRules.ValidateMeasuredSession(normalized!.Domain, cutover); }
                catch (Exception exception) { conflicts.Add(new("session_cutover_conflict", "session", entityId, exception.Message)); continue; }
            }

            if (localSessions.TryGetValue(item.Id, out var existing))
            {
                if (SessionEquivalent(existing, normalized!)) sessionDuplicates++;
                else conflicts.Add(new("session_uuid_conflict", "session", entityId, "This session UUID already exists locally with different data."));
                continue;
            }

            if (OverlapsAnySession(normalized!, localSessions.Values) || OverlapsAnySession(normalized!, sessionInserts))
            {
                conflicts.Add(new("session_overlap", "session", entityId, "This measured session overlaps another measured session for the same game and would double-count playtime."));
                continue;
            }
            if (OverlapsGapEvidence(normalized!, localHistorical.Values))
            {
                conflicts.Add(new("session_gap_overlap", "session", entityId, "This measured session overlaps existing gap-recovery evidence for the same game."));
                continue;
            }
            sessionInserts.Add(normalized!);
        }

        var historicalInserts = new List<NormalizedHistorical>();
        var historicalDuplicates = 0;
        var fileHistoricalIds = new HashSet<Guid>();
        foreach (var item in historical)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entityId = item.Id.ToString("D");
            if (!fileHistoricalIds.Add(item.Id))
            {
                conflicts.Add(new("duplicate_historical_id_in_file", "historical_evidence", entityId, "The portable file contains the same historical evidence UUID more than once."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId))
            {
                conflicts.Add(new("unknown_game", "historical_evidence", entityId, $"Historical evidence references unknown game UUID {item.GameId:D}."));
                continue;
            }
            if (!TryNormalizeHistorical(item, out var normalized, out var error))
            {
                conflicts.Add(new("invalid_historical_evidence", "historical_evidence", entityId, error!));
                continue;
            }
            if (effectiveCutover is { } cutover)
            {
                try { PlaytimeTimelineRules.ValidateAgainstCutover(normalized!.Domain, cutover); }
                catch (Exception exception) { conflicts.Add(new("historical_cutover_conflict", "historical_evidence", entityId, exception.Message)); continue; }
            }

            if (localHistorical.TryGetValue(item.Id, out var existing))
            {
                if (HistoricalEquivalent(existing, normalized!)) historicalDuplicates++;
                else conflicts.Add(new("historical_uuid_conflict", "historical_evidence", entityId, "This historical evidence UUID already exists locally with different data."));
                continue;
            }

            if (OverlapsAnyHistorical(normalized!, localHistorical.Values) || OverlapsAnyHistorical(normalized!, historicalInserts))
            {
                conflicts.Add(new("historical_overlap", "historical_evidence", entityId, "This historical interval overlaps other historical evidence for the same game and could double-count playtime."));
                continue;
            }
            if (normalized!.Domain.Kind == EvidenceKind.GapRecovery &&
                (OverlapsAnyMeasured(normalized, localSessions.Values) || OverlapsAnyMeasured(normalized, sessionInserts)))
            {
                conflicts.Add(new("gap_session_overlap", "historical_evidence", entityId, "Gap-recovery evidence overlaps a measured session for the same game."));
                continue;
            }
            historicalInserts.Add(normalized);
        }

        // Catch a gap item that was accepted after a new session was accepted earlier in the file.
        foreach (var session in sessionInserts)
        {
            if (historicalInserts.Any(history =>
                    history.Domain.Kind == EvidenceKind.GapRecovery &&
                    history.Domain.GameId == session.Domain.GameId &&
                    PlaytimeTimelineRules.Overlaps(
                        history.Domain.PeriodStartUtc,
                        history.Domain.PeriodEndUtc,
                        session.Domain.StartedAtUtc,
                        session.Domain.EndedAtUtc)))
            {
                conflicts.Add(new("session_gap_overlap", "session", session.Domain.Id.ToString("D"), "Imported measured session overlaps imported gap-recovery evidence."));
            }
        }

        var observationUpserts = new List<PortableAchievementObservation>();
        var fileObservationGames = new HashSet<Guid>();
        foreach (var item in observations)
        {
            if (!fileObservationGames.Add(item.GameId))
            {
                conflicts.Add(new("duplicate_achievement_observation", "achievement_observation", item.GameId.ToString("D"), "The portable file contains more than one achievement observation state for this game."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId) || string.IsNullOrWhiteSpace(item.LastSource) || item.LastObservedAtUtc < item.InitializedAtUtc)
            {
                conflicts.Add(new("invalid_achievement_observation", "achievement_observation", item.GameId.ToString("D"), "Achievement observation state is invalid or references an unknown game."));
                continue;
            }
            observationUpserts.Add(item.Normalize());
        }

        var achievementUpserts = new List<PortableAchievement>();
        var newAchievements = 0;
        var updatedAchievements = 0;
        var fileAchievementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in achievements)
        {
            var key = AchievementKey(item.GameId, item.ApiName);
            if (!fileAchievementKeys.Add(key))
            {
                conflicts.Add(new("duplicate_achievement_in_file", "achievement", key, "The portable file contains the same achievement more than once."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId) || !TryValidateAchievement(item, out var achievementError))
            {
                conflicts.Add(new("invalid_achievement", "achievement", key, achievementError ?? "Achievement references an unknown game."));
                continue;
            }

            var normalized = item.Normalize();
            if (!localAchievements.TryGetValue(key, out var existing))
            {
                newAchievements++;
                achievementUpserts.Add(normalized);
                continue;
            }

            var merged = MergeAchievement(existing, normalized);
            if (!AchievementEquivalent(existing, merged))
            {
                updatedAchievements++;
                achievementUpserts.Add(merged);
            }
        }

        var milestoneUpserts = new List<PortableAchievementMilestone>();
        var fileMilestoneGames = new HashSet<Guid>();
        foreach (var item in milestones)
        {
            var entityId = item.GameId.ToString("D");
            if (!fileMilestoneGames.Add(item.GameId))
            {
                conflicts.Add(new("duplicate_completion_milestone", "achievement_completion", entityId, "The portable file contains more than one completion milestone for this game."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId) || string.IsNullOrWhiteSpace(item.Source))
            {
                conflicts.Add(new("invalid_completion_milestone", "achievement_completion", entityId, "Completion milestone is invalid or references an unknown game."));
                continue;
            }
            var normalized = item.Normalize();
            if (!localMilestones.TryGetValue(item.GameId, out var existingMilestone))
            {
                milestoneUpserts.Add(normalized);
            }
            else if (!existingMilestone.IsObservedTimeFallback && !normalized.IsObservedTimeFallback &&
                     existingMilestone.CompletedAtUtc != normalized.CompletedAtUtc)
            {
                conflicts.Add(new("completion_time_conflict", "achievement_completion", entityId, "Local and imported exact 100% completion timestamps disagree."));
            }
            else if (existingMilestone.IsObservedTimeFallback && !normalized.IsObservedTimeFallback)
            {
                milestoneUpserts.Add(normalized);
            }
            else if (existingMilestone.IsObservedTimeFallback && normalized.IsObservedTimeFallback &&
                     normalized.CompletedAtUtc < existingMilestone.CompletedAtUtc)
            {
                milestoneUpserts.Add(normalized);
            }
        }

        var preview = new GameHoursPortableImportPreview(
            sourcePath,
            document.FormatVersion,
            games.Length,
            sessions.Length,
            historical.Length,
            achievements.Length,
            gameInserts.Count,
            gameUpdates.Count,
            sessionInserts.Count,
            sessionDuplicates,
            historicalInserts.Count,
            historicalDuplicates,
            newAchievements,
            updatedAchievements,
            conflicts.Count,
            conflicts.ToArray());

        return new ImportPlan(
            preview,
            localCutover is null ? document.TrackingStartedAtUtc?.ToUniversalTime() : null,
            gameInserts,
            gameUpdates,
            sessionInserts,
            historicalInserts,
            observationUpserts,
            achievementUpserts,
            milestoneUpserts);
    }

    private static async Task ApplyPlanAsync(
        ImportPlan plan,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (plan.CutoverToSet is { } cutover)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO tracking_state(singleton_id, tracking_started_at_utc) VALUES(1, $cutover) ON CONFLICT(singleton_id) DO NOTHING;";
            command.Parameters.AddWithValue("$cutover", SerializeUtc(cutover));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var game in plan.GameInserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO games(id, title, catalog_game_id, created_at_utc, updated_at_utc) VALUES($id, $title, NULL, $created, $updated);";
            command.Parameters.AddWithValue("$id", game.Id.ToString("D"));
            command.Parameters.AddWithValue("$title", game.Title);
            command.Parameters.AddWithValue("$created", SerializeUtc(game.CreatedAtUtc));
            command.Parameters.AddWithValue("$updated", SerializeUtc(game.UpdatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var game in plan.GameUpdates)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE games SET title = $title, updated_at_utc = $updated WHERE id = $id;";
            command.Parameters.AddWithValue("$id", game.Id.ToString("D"));
            command.Parameters.AddWithValue("$title", game.Title);
            command.Parameters.AddWithValue("$updated", SerializeUtc(game.UpdatedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var session in plan.SessionInserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions(id, game_id, started_at_utc, ended_at_utc, duration_ms, capture_method, confidence, end_reason, created_at_utc)
                VALUES($id, $game, $start, $end, $duration, $method, $confidence, $reason, $created);
                """;
            command.Parameters.AddWithValue("$id", session.Domain.Id.ToString("D"));
            command.Parameters.AddWithValue("$game", session.Domain.GameId.ToString("D"));
            command.Parameters.AddWithValue("$start", SerializeUtc(session.Domain.StartedAtUtc));
            command.Parameters.AddWithValue("$end", SerializeUtc(session.Domain.EndedAtUtc));
            command.Parameters.AddWithValue("$duration", session.DurationMilliseconds);
            command.Parameters.AddWithValue("$method", (int)session.Domain.CaptureMethod);
            command.Parameters.AddWithValue("$confidence", (int)session.Domain.Confidence);
            command.Parameters.AddWithValue("$reason", (object?)session.Domain.EndReason ?? DBNull.Value);
            command.Parameters.AddWithValue("$created", SerializeUtc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var history in plan.HistoricalInserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO historical_evidence(id, game_id, source, evidence_kind, metric, confidence, period_start_utc, period_end_utc, duration_ms, created_at_utc)
                VALUES($id, $game, $source, $kind, $metric, $confidence, $start, $end, $duration, $created);
                """;
            command.Parameters.AddWithValue("$id", history.Domain.Id.ToString("D"));
            command.Parameters.AddWithValue("$game", history.Domain.GameId.ToString("D"));
            command.Parameters.AddWithValue("$source", (int)history.Domain.Source);
            command.Parameters.AddWithValue("$kind", (int)history.Domain.Kind);
            command.Parameters.AddWithValue("$metric", (int)history.Domain.Metric);
            command.Parameters.AddWithValue("$confidence", (int)history.Domain.Confidence);
            command.Parameters.AddWithValue("$start", SerializeUtc(history.Domain.PeriodStartUtc));
            command.Parameters.AddWithValue("$end", SerializeUtc(history.Domain.PeriodEndUtc));
            command.Parameters.AddWithValue("$duration", history.DurationMilliseconds);
            command.Parameters.AddWithValue("$created", SerializeUtc(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var observation in plan.ObservationUpserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO achievement_observation_state(game_id, initialized_at_utc, last_observed_at_utc, last_source, has_complete_catalogue)
                VALUES($game, $initialized, $last, $source, $complete)
                ON CONFLICT(game_id) DO UPDATE SET
                    initialized_at_utc = MIN(achievement_observation_state.initialized_at_utc, excluded.initialized_at_utc),
                    last_source = CASE WHEN excluded.last_observed_at_utc >= achievement_observation_state.last_observed_at_utc THEN excluded.last_source ELSE achievement_observation_state.last_source END,
                    last_observed_at_utc = MAX(achievement_observation_state.last_observed_at_utc, excluded.last_observed_at_utc),
                    has_complete_catalogue = MAX(achievement_observation_state.has_complete_catalogue, excluded.has_complete_catalogue);
                """;
            command.Parameters.AddWithValue("$game", observation.GameId.ToString("D"));
            command.Parameters.AddWithValue("$initialized", SerializeUtc(observation.InitializedAtUtc));
            command.Parameters.AddWithValue("$last", SerializeUtc(observation.LastObservedAtUtc));
            command.Parameters.AddWithValue("$source", observation.LastSource);
            command.Parameters.AddWithValue("$complete", observation.HasCompleteCatalogue ? 1 : 0);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var achievement in plan.AchievementUpserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO achievement_states(game_id, api_name, display_name, description, hidden, is_unlocked, unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc, first_unlocked_seen_at_utc)
                VALUES($game, $api, $display, $description, $hidden, $unlocked, $unlocked_at, $source, $first_seen, $last_seen, $first_unlocked_seen)
                ON CONFLICT(game_id, api_name) DO UPDATE SET
                    display_name = excluded.display_name,
                    description = excluded.description,
                    hidden = excluded.hidden,
                    is_unlocked = excluded.is_unlocked,
                    unlocked_at_utc = excluded.unlocked_at_utc,
                    source = excluded.source,
                    first_seen_at_utc = excluded.first_seen_at_utc,
                    last_seen_at_utc = excluded.last_seen_at_utc,
                    first_unlocked_seen_at_utc = excluded.first_unlocked_seen_at_utc;
                """;
            command.Parameters.AddWithValue("$game", achievement.GameId.ToString("D"));
            command.Parameters.AddWithValue("$api", achievement.ApiName);
            command.Parameters.AddWithValue("$display", achievement.DisplayName);
            command.Parameters.AddWithValue("$description", achievement.Description);
            command.Parameters.AddWithValue("$hidden", achievement.Hidden ? 1 : 0);
            command.Parameters.AddWithValue("$unlocked", achievement.IsUnlocked ? 1 : 0);
            command.Parameters.AddWithValue("$unlocked_at", achievement.UnlockedAtUtc is { } unlockedAt ? SerializeUtc(unlockedAt) : DBNull.Value);
            command.Parameters.AddWithValue("$source", achievement.Source);
            command.Parameters.AddWithValue("$first_seen", SerializeUtc(achievement.FirstSeenAtUtc));
            command.Parameters.AddWithValue("$last_seen", SerializeUtc(achievement.LastSeenAtUtc));
            command.Parameters.AddWithValue("$first_unlocked_seen", achievement.FirstUnlockedSeenAtUtc is { } firstUnlocked ? SerializeUtc(firstUnlocked) : DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var milestone in plan.MilestoneUpserts)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO achievement_completion_milestones(game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc)
                VALUES($game, $completed, $fallback, $source, $recorded)
                ON CONFLICT(game_id) DO UPDATE SET
                    completed_at_utc = excluded.completed_at_utc,
                    is_observed_time_fallback = excluded.is_observed_time_fallback,
                    source = excluded.source,
                    recorded_at_utc = excluded.recorded_at_utc;
                """;
            command.Parameters.AddWithValue("$game", milestone.GameId.ToString("D"));
            command.Parameters.AddWithValue("$completed", SerializeUtc(milestone.CompletedAtUtc));
            command.Parameters.AddWithValue("$fallback", milestone.IsObservedTimeFallback ? 1 : 0);
            command.Parameters.AddWithValue("$source", milestone.Source);
            command.Parameters.AddWithValue("$recorded", SerializeUtc(milestone.RecordedAtUtc));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static bool TryNormalizeSession(PortableSession item, out NormalizedSession? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (!TryParseWireEnum<CaptureMethod>(item.CaptureMethod, out var method) ||
            !TryParseWireEnum<Confidence>(item.Confidence, out var confidence))
        {
            error = "Session capture_method or confidence is unknown.";
            return false;
        }
        try
        {
            var domain = new PlaySession(item.Id, item.GameId, item.StartedAtUtc, item.EndedAtUtc, method, confidence, item.EndReason);
            var expected = checked((long)Math.Round(domain.Duration.TotalMilliseconds));
            if (item.DurationMilliseconds != expected)
            {
                error = $"Session duration_milliseconds ({item.DurationMilliseconds}) does not match its UTC interval ({expected}).";
                return false;
            }
            normalized = new NormalizedSession(domain, expected);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryNormalizeHistorical(PortableHistoricalEvidence item, out NormalizedHistorical? normalized, out string? error)
    {
        normalized = null;
        error = null;
        if (!TryParseWireEnum<HistoricalSource>(item.Source, out var source) ||
            !TryParseWireEnum<EvidenceKind>(item.EvidenceKind, out var kind) ||
            !TryParseWireEnum<PlaytimeMetric>(item.Metric, out var metric) ||
            !TryParseWireEnum<Confidence>(item.Confidence, out var confidence))
        {
            error = "Historical source, evidence_kind, metric or confidence is unknown.";
            return false;
        }
        try
        {
            var duration = TimeSpan.FromMilliseconds(item.DurationMilliseconds);
            var domain = new HistoricalEvidence(item.Id, item.GameId, source, kind, metric, confidence, item.PeriodStartUtc, item.PeriodEndUtc, duration);
            normalized = new NormalizedHistorical(domain, item.DurationMilliseconds);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException or OverflowException)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool TryValidateAchievement(PortableAchievement item, out string? error)
    {
        error = null;
        if (item.GameId == Guid.Empty || string.IsNullOrWhiteSpace(item.ApiName) || string.IsNullOrWhiteSpace(item.Source))
        {
            error = "Achievement game_id, api_name and source must be present.";
            return false;
        }
        if (item.LastSeenAtUtc < item.FirstSeenAtUtc)
        {
            error = "Achievement last_seen_at_utc cannot be before first_seen_at_utc.";
            return false;
        }
        if (!item.IsUnlocked && (item.UnlockedAtUtc is not null || item.FirstUnlockedSeenAtUtc is not null))
        {
            error = "A locked achievement cannot contain unlock timestamps.";
            return false;
        }
        return true;
    }

    private static PortableAchievement MergeAchievement(LocalAchievement existing, PortableAchievement incoming)
    {
        var unlocked = existing.IsUnlocked || incoming.IsUnlocked;
        var unlockedAt = Earliest(existing.UnlockedAtUtc, incoming.UnlockedAtUtc);
        var firstUnlockedSeen = Earliest(existing.FirstUnlockedSeenAtUtc, incoming.FirstUnlockedSeenAtUtc);
        var incomingIsNewer = incoming.LastSeenAtUtc >= existing.LastSeenAtUtc;
        var display = incomingIsNewer && !string.IsNullOrWhiteSpace(incoming.DisplayName)
            ? incoming.DisplayName
            : existing.DisplayName;
        var description = incomingIsNewer && !string.IsNullOrWhiteSpace(incoming.Description)
            ? incoming.Description
            : existing.Description;
        return new PortableAchievement(
            incoming.GameId,
            incoming.ApiName,
            display,
            description,
            incomingIsNewer ? incoming.Hidden : existing.Hidden,
            unlocked,
            unlocked ? unlockedAt : null,
            incomingIsNewer ? incoming.Source : existing.Source,
            existing.FirstSeenAtUtc <= incoming.FirstSeenAtUtc ? existing.FirstSeenAtUtc : incoming.FirstSeenAtUtc,
            existing.LastSeenAtUtc >= incoming.LastSeenAtUtc ? existing.LastSeenAtUtc : incoming.LastSeenAtUtc,
            unlocked ? firstUnlockedSeen : null);
    }

    private static bool AchievementEquivalent(LocalAchievement existing, PortableAchievement merged) =>
        string.Equals(existing.DisplayName, merged.DisplayName, StringComparison.Ordinal) &&
        string.Equals(existing.Description, merged.Description, StringComparison.Ordinal) &&
        existing.Hidden == merged.Hidden && existing.IsUnlocked == merged.IsUnlocked &&
        existing.UnlockedAtUtc == merged.UnlockedAtUtc && string.Equals(existing.Source, merged.Source, StringComparison.Ordinal) &&
        existing.FirstSeenAtUtc == merged.FirstSeenAtUtc && existing.LastSeenAtUtc == merged.LastSeenAtUtc &&
        existing.FirstUnlockedSeenAtUtc == merged.FirstUnlockedSeenAtUtc;

    private static bool SessionEquivalent(NormalizedSession left, NormalizedSession right) =>
        left.Domain.GameId == right.Domain.GameId && left.Domain.StartedAtUtc == right.Domain.StartedAtUtc &&
        left.Domain.EndedAtUtc == right.Domain.EndedAtUtc && left.Domain.CaptureMethod == right.Domain.CaptureMethod &&
        left.Domain.Confidence == right.Domain.Confidence && string.Equals(left.Domain.EndReason, right.Domain.EndReason, StringComparison.Ordinal) &&
        left.DurationMilliseconds == right.DurationMilliseconds;

    private static bool HistoricalEquivalent(NormalizedHistorical left, NormalizedHistorical right) =>
        left.Domain.GameId == right.Domain.GameId && left.Domain.Source == right.Domain.Source && left.Domain.Kind == right.Domain.Kind &&
        left.Domain.Metric == right.Domain.Metric && left.Domain.Confidence == right.Domain.Confidence &&
        left.Domain.PeriodStartUtc == right.Domain.PeriodStartUtc && left.Domain.PeriodEndUtc == right.Domain.PeriodEndUtc &&
        left.DurationMilliseconds == right.DurationMilliseconds;

    private static bool OverlapsAnySession(NormalizedSession candidate, IEnumerable<NormalizedSession> others) =>
        others.Any(other => other.Domain.GameId == candidate.Domain.GameId &&
            PlaytimeTimelineRules.Overlaps(candidate.Domain.StartedAtUtc, candidate.Domain.EndedAtUtc, other.Domain.StartedAtUtc, other.Domain.EndedAtUtc));

    private static bool OverlapsGapEvidence(NormalizedSession candidate, IEnumerable<NormalizedHistorical> history) =>
        history.Any(other => other.Domain.GameId == candidate.Domain.GameId && other.Domain.Kind == EvidenceKind.GapRecovery &&
            PlaytimeTimelineRules.Overlaps(candidate.Domain.StartedAtUtc, candidate.Domain.EndedAtUtc, other.Domain.PeriodStartUtc, other.Domain.PeriodEndUtc));

    private static bool OverlapsAnyHistorical(NormalizedHistorical candidate, IEnumerable<NormalizedHistorical> others) =>
        others.Any(other => other.Domain.GameId == candidate.Domain.GameId &&
            PlaytimeTimelineRules.Overlaps(candidate.Domain.PeriodStartUtc, candidate.Domain.PeriodEndUtc, other.Domain.PeriodStartUtc, other.Domain.PeriodEndUtc));

    private static bool OverlapsAnyMeasured(NormalizedHistorical candidate, IEnumerable<NormalizedSession> sessions) =>
        sessions.Any(session => session.Domain.GameId == candidate.Domain.GameId &&
            PlaytimeTimelineRules.Overlaps(candidate.Domain.PeriodStartUtc, candidate.Domain.PeriodEndUtc, session.Domain.StartedAtUtc, session.Domain.EndedAtUtc));

    private static bool TryParseWireEnum<TEnum>(string? value, out TEnum result) where TEnum : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var normalized = value.Replace("_", string.Empty, StringComparison.Ordinal).Trim();
        foreach (var name in Enum.GetNames<TEnum>())
        {
            if (string.Equals(name, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.TryParse(name, out result);
            }
        }
        return false;
    }

    private static DateTimeOffset? Earliest(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null) return right;
        if (right is null) return left;
        return left <= right ? left : right;
    }

    private static string AchievementKey(Guid gameId, string? apiName) =>
        $"{gameId:D}|{(apiName ?? string.Empty).Trim().ToUpperInvariant()}";

    private static string SerializeUtc(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static async Task<DateTimeOffset?> ReadLocalCutoverAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id = 1;";
        var value = await command.ExecuteScalarAsync(token);
        return value is null or DBNull ? null : ParseUtc(Convert.ToString(value, CultureInfo.InvariantCulture)!);
    }

    private static async Task<Dictionary<Guid, LocalGame>> ReadLocalGamesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<Guid, LocalGame>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, title, created_at_utc, updated_at_utc FROM games;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var item = new LocalGame(Guid.Parse(reader.GetString(0)), reader.GetString(1), ParseUtc(reader.GetString(2)), ParseUtc(reader.GetString(3)));
            result[item.Id] = item;
        }
        return result;
    }

    private static async Task<Dictionary<Guid, NormalizedSession>> ReadLocalSessionsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<Guid, NormalizedSession>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, game_id, started_at_utc, ended_at_utc, duration_ms, capture_method, confidence, end_reason FROM sessions;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var domain = new PlaySession(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), ParseUtc(reader.GetString(2)), ParseUtc(reader.GetString(3)), (CaptureMethod)reader.GetInt32(5), (Confidence)reader.GetInt32(6), reader.IsDBNull(7) ? null : reader.GetString(7));
            result[domain.Id] = new NormalizedSession(domain, reader.GetInt64(4));
        }
        return result;
    }

    private static async Task<Dictionary<Guid, NormalizedHistorical>> ReadLocalHistoricalAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<Guid, NormalizedHistorical>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT id, game_id, source, evidence_kind, metric, confidence, period_start_utc, period_end_utc, duration_ms FROM historical_evidence;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var domain = new HistoricalEvidence(Guid.Parse(reader.GetString(0)), Guid.Parse(reader.GetString(1)), (HistoricalSource)reader.GetInt32(2), (EvidenceKind)reader.GetInt32(3), (PlaytimeMetric)reader.GetInt32(4), (Confidence)reader.GetInt32(5), ParseUtc(reader.GetString(6)), ParseUtc(reader.GetString(7)), TimeSpan.FromMilliseconds(reader.GetInt64(8)));
            result[domain.Id] = new NormalizedHistorical(domain, reader.GetInt64(8));
        }
        return result;
    }

    private static async Task<Dictionary<string, LocalAchievement>> ReadLocalAchievementsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<string, LocalAchievement>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT game_id, api_name, display_name, description, hidden, is_unlocked, unlocked_at_utc, source, first_seen_at_utc, last_seen_at_utc, first_unlocked_seen_at_utc FROM achievement_states;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var gameId = Guid.Parse(reader.GetString(0));
            var api = reader.GetString(1);
            var item = new LocalAchievement(gameId, api, reader.GetString(2), reader.GetString(3), reader.GetInt64(4) != 0, reader.GetInt64(5) != 0, reader.IsDBNull(6) ? null : ParseUtc(reader.GetString(6)), reader.GetString(7), ParseUtc(reader.GetString(8)), ParseUtc(reader.GetString(9)), reader.IsDBNull(10) ? null : ParseUtc(reader.GetString(10)));
            result[AchievementKey(gameId, api)] = item;
        }
        return result;
    }

    private static async Task<Dictionary<Guid, PortableAchievementObservation>> ReadLocalObservationsAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<Guid, PortableAchievementObservation>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT game_id, initialized_at_utc, last_observed_at_utc, last_source, has_complete_catalogue FROM achievement_observation_state;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var item = new PortableAchievementObservation(Guid.Parse(reader.GetString(0)), ParseUtc(reader.GetString(1)), ParseUtc(reader.GetString(2)), reader.GetString(3), reader.GetInt64(4) != 0);
            result[item.GameId] = item;
        }
        return result;
    }

    private static async Task<Dictionary<Guid, PortableAchievementMilestone>> ReadLocalMilestonesAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken token)
    {
        var result = new Dictionary<Guid, PortableAchievementMilestone>();
        await using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT game_id, completed_at_utc, is_observed_time_fallback, source, recorded_at_utc FROM achievement_completion_milestones;";
        await using var reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            var item = new PortableAchievementMilestone(Guid.Parse(reader.GetString(0)), ParseUtc(reader.GetString(1)), reader.GetInt64(2) != 0, reader.GetString(3), ParseUtc(reader.GetString(4)));
            result[item.GameId] = item;
        }
        return result;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true
    };

    private sealed record ImportPlan(
        GameHoursPortableImportPreview Preview,
        DateTimeOffset? CutoverToSet,
        IReadOnlyList<PortableGame> GameInserts,
        IReadOnlyList<PortableGame> GameUpdates,
        IReadOnlyList<NormalizedSession> SessionInserts,
        IReadOnlyList<NormalizedHistorical> HistoricalInserts,
        IReadOnlyList<PortableAchievementObservation> ObservationUpserts,
        IReadOnlyList<PortableAchievement> AchievementUpserts,
        IReadOnlyList<PortableAchievementMilestone> MilestoneUpserts);

    private sealed record LocalGame(Guid Id, string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
    private sealed record NormalizedSession(PlaySession Domain, long DurationMilliseconds);
    private sealed record NormalizedHistorical(HistoricalEvidence Domain, long DurationMilliseconds);
    private sealed record LocalAchievement(Guid GameId, string ApiName, string DisplayName, string Description, bool Hidden, bool IsUnlocked, DateTimeOffset? UnlockedAtUtc, string Source, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? FirstUnlockedSeenAtUtc);

    private sealed class PortableImportDocument
    {
        public int FormatVersion { get; init; }
        public DateTimeOffset ExportedAtUtc { get; init; }
        public int SourceSchemaVersion { get; init; }
        public DateTimeOffset? TrackingStartedAtUtc { get; init; }
        public PortableGame[]? Games { get; init; }
        public PortableSession[]? Sessions { get; init; }
        public PortableHistoricalEvidence[]? HistoricalEvidence { get; init; }
        public PortableAchievementObservation[]? AchievementObservations { get; init; }
        public PortableAchievement[]? Achievements { get; init; }
        public PortableAchievementMilestone[]? AchievementCompletionMilestones { get; init; }
    }

    private sealed record PortableGame(Guid Id, string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc);
    private sealed record PortableSession(Guid Id, Guid GameId, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, long DurationMilliseconds, string CaptureMethod, string Confidence, string? EndReason);
    private sealed record PortableHistoricalEvidence(Guid Id, Guid GameId, string Source, string EvidenceKind, string Metric, string Confidence, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, long DurationMilliseconds);
    private sealed record PortableAchievementObservation(Guid GameId, DateTimeOffset InitializedAtUtc, DateTimeOffset LastObservedAtUtc, string LastSource, bool HasCompleteCatalogue)
    {
        public PortableAchievementObservation Normalize() => this with { InitializedAtUtc = InitializedAtUtc.ToUniversalTime(), LastObservedAtUtc = LastObservedAtUtc.ToUniversalTime(), LastSource = LastSource.Trim() };
    }
    private sealed record PortableAchievement(Guid GameId, string ApiName, string DisplayName, string Description, bool Hidden, bool IsUnlocked, DateTimeOffset? UnlockedAtUtc, string Source, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? FirstUnlockedSeenAtUtc)
    {
        public PortableAchievement Normalize() => this with
        {
            ApiName = ApiName.Trim(),
            DisplayName = (DisplayName ?? string.Empty).Trim(),
            Description = (Description ?? string.Empty).Trim(),
            Source = Source.Trim(),
            UnlockedAtUtc = UnlockedAtUtc?.ToUniversalTime(),
            FirstSeenAtUtc = FirstSeenAtUtc.ToUniversalTime(),
            LastSeenAtUtc = LastSeenAtUtc.ToUniversalTime(),
            FirstUnlockedSeenAtUtc = FirstUnlockedSeenAtUtc?.ToUniversalTime()
        };
    }
    private sealed record PortableAchievementMilestone(Guid GameId, DateTimeOffset CompletedAtUtc, bool IsObservedTimeFallback, string Source, DateTimeOffset RecordedAtUtc)
    {
        public PortableAchievementMilestone Normalize() => this with { CompletedAtUtc = CompletedAtUtc.ToUniversalTime(), Source = Source.Trim(), RecordedAtUtc = RecordedAtUtc.ToUniversalTime() };
    }
}
