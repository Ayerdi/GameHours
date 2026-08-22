using System.Globalization;
using System.Text.Json;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;
using Microsoft.Data.Sqlite;

namespace GameHours.Storage.Portability;

public sealed record GameHoursPortableImportConflict(string Code, string EntityType, string EntityId, string Message);

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

public sealed record GameHoursPortableImportResult(GameHoursPortableImportPreview Preview, DateTimeOffset ImportedAtUtc);

public sealed class GameHoursPortableImportConflictException : Exception
{
    public GameHoursPortableImportPreview Preview { get; }

    public GameHoursPortableImportConflictException(GameHoursPortableImportPreview preview)
        : base($"Portable import contains {preview.ConflictCount} conflict(s) and was not applied.") => Preview = preview;
}

/// <summary>
/// Imports portable format v1 without guessing identities or accepting timeline double counting.
/// AnalyzeAsync never writes. ImportAsync rebuilds the same plan inside the transaction before apply.
/// </summary>
public sealed class GameHoursPortableImportService
{
    private readonly GameHoursDatabase _database;

    public GameHoursPortableImportService(GameHoursDatabase database) =>
        _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<GameHoursPortableImportPreview> AnalyzeAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = NormalizeSource(sourcePath);
        var document = await ReadAsync(source, cancellationToken);
        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await BuildPlanAsync(source, document, connection, transaction, cancellationToken);
        await transaction.RollbackAsync(cancellationToken);
        return plan.Preview;
    }

    public async Task<GameHoursPortableImportResult> ImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        var source = NormalizeSource(sourcePath);
        var document = await ReadAsync(source, cancellationToken);
        await using var connection = _database.OpenConnection();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var plan = await BuildPlanAsync(source, document, connection, transaction, cancellationToken);
        if (!plan.Preview.CanImport)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new GameHoursPortableImportConflictException(plan.Preview);
        }

        await ApplyAsync(plan, connection, transaction, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(plan.Preview, DateTimeOffset.UtcNow);
    }

    private static async Task<Plan> BuildPlanAsync(
        string sourcePath,
        Document document,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken token)
    {
        var conflicts = new List<GameHoursPortableImportConflict>();
        var games = document.Games ?? [];
        var sessions = document.Sessions ?? [];
        var history = document.HistoricalEvidence ?? [];
        var achievements = document.Achievements ?? [];
        var observations = document.AchievementObservations ?? [];
        var milestones = document.AchievementCompletionMilestones ?? [];

        if (document.FormatVersion != GameHoursDataPortabilityService.CurrentExportFormatVersion)
            conflicts.Add(new("unsupported_format_version", "document", document.FormatVersion.ToString(CultureInfo.InvariantCulture), $"Format v{document.FormatVersion} is not supported."));

        var localGames = await ReadGamesAsync(connection, transaction, token);
        var localSessions = await ReadSessionsAsync(connection, transaction, token);
        var localHistory = await ReadHistoryAsync(connection, transaction, token);
        var localAchievements = await ReadAchievementsAsync(connection, transaction, token);
        var localCutover = await ReadCutoverAsync(connection, transaction, token);

        var insertGames = new List<GameRow>();
        var updateGames = new List<GameRow>();
        var validGameIds = new HashSet<Guid>(localGames.Keys);
        var fileGameIds = new HashSet<Guid>();
        var fileTitles = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in games)
        {
            var game = raw.Normalize();
            var id = game.Id.ToString("D");
            if (game.Id == Guid.Empty || string.IsNullOrWhiteSpace(game.Title))
            {
                conflicts.Add(new("invalid_game", "game", id, "Game id and title are required."));
                continue;
            }
            if (!fileGameIds.Add(game.Id))
            {
                conflicts.Add(new("duplicate_game_id_in_file", "game", id, "The same game UUID occurs more than once."));
                continue;
            }
            if (fileTitles.TryGetValue(game.Title, out var fileOther) && fileOther != game.Id)
            {
                conflicts.Add(new("ambiguous_game_title_in_file", "game", id, $"Title '{game.Title}' belongs to multiple UUIDs in the file."));
                continue;
            }
            fileTitles[game.Title] = game.Id;

            var titleOther = localGames.Values.FirstOrDefault(x => x.Id != game.Id && string.Equals(x.Title, game.Title, StringComparison.OrdinalIgnoreCase));
            if (titleOther is not null)
            {
                conflicts.Add(new("game_identity_conflict", "game", id, $"'{game.Title}' already exists with GameHours UUID {titleOther.Id:D}; import v1 will not guess identity."));
                continue;
            }

            validGameIds.Add(game.Id);
            if (!localGames.TryGetValue(game.Id, out var local)) insertGames.Add(game);
            else if (!string.Equals(local.Title, game.Title, StringComparison.Ordinal) && game.UpdatedAtUtc > local.UpdatedAtUtc) updateGames.Add(game);
        }

        var effectiveCutover = localCutover ?? document.TrackingStartedAtUtc?.ToUniversalTime();
        if ((sessions.Length > 0 || history.Length > 0) && effectiveCutover is null)
            conflicts.Add(new("missing_tracking_cutover", "timeline", "tracking_started_at_utc", "Time data requires a tracking cutover."));

        var insertSessions = new List<SessionRow>();
        var duplicateSessions = 0;
        var fileSessionIds = new HashSet<Guid>();
        foreach (var raw in sessions)
        {
            var id = raw.Id.ToString("D");
            if (!fileSessionIds.Add(raw.Id))
            {
                conflicts.Add(new("duplicate_session_id_in_file", "session", id, "The same session UUID occurs more than once."));
                continue;
            }
            if (!validGameIds.Contains(raw.GameId))
            {
                conflicts.Add(new("unknown_game", "session", id, $"Unknown game UUID {raw.GameId:D}."));
                continue;
            }
            if (!TrySession(raw, out var session, out var sessionError))
            {
                conflicts.Add(new("invalid_session", "session", id, sessionError!));
                continue;
            }
            if (effectiveCutover is { } cutover)
            {
                try { PlaytimeTimelineRules.ValidateMeasuredSession(session!.Domain, cutover); }
                catch (Exception ex) { conflicts.Add(new("session_cutover_conflict", "session", id, ex.Message)); continue; }
            }
            if (localSessions.TryGetValue(raw.Id, out var existing))
            {
                if (SessionEqual(existing, session!)) duplicateSessions++;
                else conflicts.Add(new("session_uuid_conflict", "session", id, "This UUID already exists with different session data."));
                continue;
            }
            if (OverlapsSession(session!, localSessions.Values) || OverlapsSession(session!, insertSessions))
            {
                conflicts.Add(new("session_overlap", "session", id, "Measured session overlaps another measured session for this game."));
                continue;
            }
            if (localHistory.Values.Any(h => h.Domain.GameId == session!.Domain.GameId && h.Domain.Kind == EvidenceKind.GapRecovery && Overlap(session.Domain.StartedAtUtc, session.Domain.EndedAtUtc, h.Domain.PeriodStartUtc, h.Domain.PeriodEndUtc)))
            {
                conflicts.Add(new("session_gap_overlap", "session", id, "Measured session overlaps existing gap-recovery evidence."));
                continue;
            }
            insertSessions.Add(session!);
        }

        var insertHistory = new List<HistoryRow>();
        var duplicateHistory = 0;
        var fileHistoryIds = new HashSet<Guid>();
        foreach (var raw in history)
        {
            var id = raw.Id.ToString("D");
            if (!fileHistoryIds.Add(raw.Id))
            {
                conflicts.Add(new("duplicate_historical_id_in_file", "historical_evidence", id, "The same evidence UUID occurs more than once."));
                continue;
            }
            if (!validGameIds.Contains(raw.GameId))
            {
                conflicts.Add(new("unknown_game", "historical_evidence", id, $"Unknown game UUID {raw.GameId:D}."));
                continue;
            }
            if (!TryHistory(raw, out var item, out var historyError))
            {
                conflicts.Add(new("invalid_historical_evidence", "historical_evidence", id, historyError!));
                continue;
            }
            if (effectiveCutover is { } cutover)
            {
                try { PlaytimeTimelineRules.ValidateAgainstCutover(item!.Domain, cutover); }
                catch (Exception ex) { conflicts.Add(new("historical_cutover_conflict", "historical_evidence", id, ex.Message)); continue; }
            }
            if (localHistory.TryGetValue(raw.Id, out var existing))
            {
                if (HistoryEqual(existing, item!)) duplicateHistory++;
                else conflicts.Add(new("historical_uuid_conflict", "historical_evidence", id, "This UUID already exists with different historical data."));
                continue;
            }
            if (OverlapsHistory(item!, localHistory.Values) || OverlapsHistory(item!, insertHistory))
            {
                conflicts.Add(new("historical_overlap", "historical_evidence", id, "Historical interval overlaps other historical evidence for this game."));
                continue;
            }
            if (item!.Domain.Kind == EvidenceKind.GapRecovery &&
                (OverlapsMeasured(item, localSessions.Values) || OverlapsMeasured(item, insertSessions)))
            {
                conflicts.Add(new("gap_session_overlap", "historical_evidence", id, "Gap-recovery evidence overlaps measured playtime."));
                continue;
            }
            insertHistory.Add(item);
        }

        var upsertAchievements = new List<AchievementRow>();
        var newAchievements = 0;
        var updatedAchievements = 0;
        var fileAchievementKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in achievements)
        {
            var item = raw.Normalize();
            var key = AchievementKey(item.GameId, item.ApiName);
            if (!fileAchievementKeys.Add(key))
            {
                conflicts.Add(new("duplicate_achievement_in_file", "achievement", key, "The same achievement occurs more than once."));
                continue;
            }
            if (!validGameIds.Contains(item.GameId) || !ValidAchievement(item, out var achievementError))
            {
                conflicts.Add(new("invalid_achievement", "achievement", key, achievementError ?? "Achievement references an unknown game."));
                continue;
            }
            if (!localAchievements.TryGetValue(key, out var local))
            {
                newAchievements++;
                upsertAchievements.Add(item);
            }
            else
            {
                var merged = MergeAchievement(local, item);
                if (!AchievementEqual(local, merged))
                {
                    updatedAchievements++;
                    upsertAchievements.Add(merged);
                }
            }
        }

        var upsertObservations = new List<ObservationRow>();
        var observationGames = new HashSet<Guid>();
        foreach (var raw in observations)
        {
            var item = raw.Normalize();
            if (!observationGames.Add(item.GameId) || !validGameIds.Contains(item.GameId) || string.IsNullOrWhiteSpace(item.LastSource) || item.LastObservedAtUtc < item.InitializedAtUtc)
            {
                conflicts.Add(new("invalid_achievement_observation", "achievement_observation", item.GameId.ToString("D"), "Observation state is duplicate, invalid, or references an unknown game."));
                continue;
            }
            upsertObservations.Add(item);
        }

        var upsertMilestones = new List<MilestoneRow>();
        var milestoneGames = new HashSet<Guid>();
        foreach (var raw in milestones)
        {
            var item = raw.Normalize();
            if (!milestoneGames.Add(item.GameId) || !validGameIds.Contains(item.GameId) || string.IsNullOrWhiteSpace(item.Source))
            {
                conflicts.Add(new("invalid_completion_milestone", "achievement_completion", item.GameId.ToString("D"), "Completion milestone is duplicate, invalid, or references an unknown game."));
                continue;
            }
            upsertMilestones.Add(item);
        }

        var preview = new GameHoursPortableImportPreview(
            sourcePath,
            document.FormatVersion,
            games.Length,
            sessions.Length,
            history.Length,
            achievements.Length,
            insertGames.Count,
            updateGames.Count,
            insertSessions.Count,
            duplicateSessions,
            insertHistory.Count,
            duplicateHistory,
            newAchievements,
            updatedAchievements,
            conflicts.Count,
            conflicts.ToArray());

        return new(
            preview,
            localCutover is null ? document.TrackingStartedAtUtc?.ToUniversalTime() : null,
            insertGames,
            updateGames,
            insertSessions,
            insertHistory,
            upsertAchievements,
            upsertObservations,
            upsertMilestones);
    }

    private static async Task ApplyAsync(Plan plan, SqliteConnection connection, SqliteTransaction tx, CancellationToken token)
    {
        if (plan.CutoverToSet is { } cutover)
            await ExecuteAsync(connection, tx, "INSERT INTO tracking_state(singleton_id, tracking_started_at_utc) VALUES(1,$value) ON CONFLICT(singleton_id) DO NOTHING;", token, ("$value", Utc(cutover)));

        foreach (var x in plan.InsertGames)
            await ExecuteAsync(connection, tx, "INSERT INTO games(id,title,catalog_game_id,created_at_utc,updated_at_utc) VALUES($id,$title,NULL,$created,$updated);", token,
                ("$id", x.Id.ToString("D")), ("$title", x.Title), ("$created", Utc(x.CreatedAtUtc)), ("$updated", Utc(x.UpdatedAtUtc)));
        foreach (var x in plan.UpdateGames)
            await ExecuteAsync(connection, tx, "UPDATE games SET title=$title, updated_at_utc=$updated WHERE id=$id;", token,
                ("$id", x.Id.ToString("D")), ("$title", x.Title), ("$updated", Utc(x.UpdatedAtUtc)));
        foreach (var x in plan.InsertSessions)
            await ExecuteAsync(connection, tx, "INSERT INTO sessions(id,game_id,started_at_utc,ended_at_utc,duration_ms,capture_method,confidence,end_reason,created_at_utc) VALUES($id,$game,$start,$end,$duration,$method,$confidence,$reason,$created);", token,
                ("$id", x.Domain.Id.ToString("D")), ("$game", x.Domain.GameId.ToString("D")), ("$start", Utc(x.Domain.StartedAtUtc)), ("$end", Utc(x.Domain.EndedAtUtc)), ("$duration", x.DurationMs), ("$method", (int)x.Domain.CaptureMethod), ("$confidence", (int)x.Domain.Confidence), ("$reason", (object?)x.Domain.EndReason ?? DBNull.Value), ("$created", Utc(DateTimeOffset.UtcNow)));
        foreach (var x in plan.InsertHistory)
            await ExecuteAsync(connection, tx, "INSERT INTO historical_evidence(id,game_id,source,evidence_kind,metric,confidence,period_start_utc,period_end_utc,duration_ms,created_at_utc) VALUES($id,$game,$source,$kind,$metric,$confidence,$start,$end,$duration,$created);", token,
                ("$id", x.Domain.Id.ToString("D")), ("$game", x.Domain.GameId.ToString("D")), ("$source", (int)x.Domain.Source), ("$kind", (int)x.Domain.Kind), ("$metric", (int)x.Domain.Metric), ("$confidence", (int)x.Domain.Confidence), ("$start", Utc(x.Domain.PeriodStartUtc)), ("$end", Utc(x.Domain.PeriodEndUtc)), ("$duration", x.DurationMs), ("$created", Utc(DateTimeOffset.UtcNow)));
        foreach (var x in plan.UpsertObservations)
            await ExecuteAsync(connection, tx, """
                INSERT INTO achievement_observation_state(game_id,initialized_at_utc,last_observed_at_utc,last_source,has_complete_catalogue)
                VALUES($game,$initialized,$last,$source,$complete)
                ON CONFLICT(game_id) DO UPDATE SET
                  initialized_at_utc=MIN(achievement_observation_state.initialized_at_utc,excluded.initialized_at_utc),
                  last_source=CASE WHEN excluded.last_observed_at_utc>=achievement_observation_state.last_observed_at_utc THEN excluded.last_source ELSE achievement_observation_state.last_source END,
                  last_observed_at_utc=MAX(achievement_observation_state.last_observed_at_utc,excluded.last_observed_at_utc),
                  has_complete_catalogue=MAX(achievement_observation_state.has_complete_catalogue,excluded.has_complete_catalogue);
                """, token, ("$game", x.GameId.ToString("D")), ("$initialized", Utc(x.InitializedAtUtc)), ("$last", Utc(x.LastObservedAtUtc)), ("$source", x.LastSource), ("$complete", x.HasCompleteCatalogue ? 1 : 0));
        foreach (var x in plan.UpsertAchievements)
            await ExecuteAsync(connection, tx, """
                INSERT INTO achievement_states(game_id,api_name,display_name,description,hidden,is_unlocked,unlocked_at_utc,source,first_seen_at_utc,last_seen_at_utc,first_unlocked_seen_at_utc)
                VALUES($game,$api,$display,$description,$hidden,$unlocked,$unlocked_at,$source,$first_seen,$last_seen,$first_unlocked_seen)
                ON CONFLICT(game_id,api_name) DO UPDATE SET display_name=excluded.display_name,description=excluded.description,hidden=excluded.hidden,is_unlocked=excluded.is_unlocked,unlocked_at_utc=excluded.unlocked_at_utc,source=excluded.source,first_seen_at_utc=excluded.first_seen_at_utc,last_seen_at_utc=excluded.last_seen_at_utc,first_unlocked_seen_at_utc=excluded.first_unlocked_seen_at_utc;
                """, token, ("$game", x.GameId.ToString("D")), ("$api", x.ApiName), ("$display", x.DisplayName), ("$description", x.Description), ("$hidden", x.Hidden ? 1 : 0), ("$unlocked", x.IsUnlocked ? 1 : 0), ("$unlocked_at", x.UnlockedAtUtc is { } u ? Utc(u) : DBNull.Value), ("$source", x.Source), ("$first_seen", Utc(x.FirstSeenAtUtc)), ("$last_seen", Utc(x.LastSeenAtUtc)), ("$first_unlocked_seen", x.FirstUnlockedSeenAtUtc is { } f ? Utc(f) : DBNull.Value));
        foreach (var x in plan.UpsertMilestones)
            await ExecuteAsync(connection, tx, """
                INSERT INTO achievement_completion_milestones(game_id,completed_at_utc,is_observed_time_fallback,source,recorded_at_utc)
                VALUES($game,$completed,$fallback,$source,$recorded)
                ON CONFLICT(game_id) DO UPDATE SET
                  completed_at_utc=CASE WHEN achievement_completion_milestones.is_observed_time_fallback=1 AND excluded.is_observed_time_fallback=0 THEN excluded.completed_at_utc WHEN achievement_completion_milestones.is_observed_time_fallback=1 AND excluded.completed_at_utc<achievement_completion_milestones.completed_at_utc THEN excluded.completed_at_utc ELSE achievement_completion_milestones.completed_at_utc END,
                  is_observed_time_fallback=MIN(achievement_completion_milestones.is_observed_time_fallback,excluded.is_observed_time_fallback),
                  source=CASE WHEN achievement_completion_milestones.is_observed_time_fallback=1 AND excluded.is_observed_time_fallback=0 THEN excluded.source ELSE achievement_completion_milestones.source END,
                  recorded_at_utc=MAX(achievement_completion_milestones.recorded_at_utc,excluded.recorded_at_utc);
                """, token, ("$game", x.GameId.ToString("D")), ("$completed", Utc(x.CompletedAtUtc)), ("$fallback", x.IsObservedTimeFallback ? 1 : 0), ("$source", x.Source), ("$recorded", Utc(x.RecordedAtUtc)));
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction tx, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = tx;
        command.CommandText = sql;
        foreach (var p in parameters) command.Parameters.AddWithValue(p.Name, p.Value);
        await command.ExecuteNonQueryAsync(token);
    }

    private static bool TrySession(SessionRow raw, out SessionValue? value, out string? error)
    {
        value = null; error = null;
        if (!TryEnum<CaptureMethod>(raw.CaptureMethod, out var method) || !TryEnum<Confidence>(raw.Confidence, out var confidence)) { error = "Unknown capture_method or confidence."; return false; }
        try
        {
            var domain = new PlaySession(raw.Id, raw.GameId, raw.StartedAtUtc, raw.EndedAtUtc, method, confidence, raw.EndReason);
            var duration = checked((long)Math.Round(domain.Duration.TotalMilliseconds));
            if (duration != raw.DurationMilliseconds) { error = "duration_milliseconds does not match the UTC interval."; return false; }
            value = new(domain, duration); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException) { error = ex.Message; return false; }
    }

    private static bool TryHistory(HistoryRow raw, out HistoryValue? value, out string? error)
    {
        value = null; error = null;
        if (!TryEnum<HistoricalSource>(raw.Source, out var source) || !TryEnum<EvidenceKind>(raw.EvidenceKind, out var kind) || !TryEnum<PlaytimeMetric>(raw.Metric, out var metric) || !TryEnum<Confidence>(raw.Confidence, out var confidence)) { error = "Unknown historical enum value."; return false; }
        try
        {
            var domain = new HistoricalEvidence(raw.Id, raw.GameId, source, kind, metric, confidence, raw.PeriodStartUtc, raw.PeriodEndUtc, TimeSpan.FromMilliseconds(raw.DurationMilliseconds));
            value = new(domain, raw.DurationMilliseconds); return true;
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException or OverflowException) { error = ex.Message; return false; }
    }

    private static bool ValidAchievement(AchievementRow x, out string? error)
    {
        error = null;
        if (x.GameId == Guid.Empty || string.IsNullOrWhiteSpace(x.ApiName) || string.IsNullOrWhiteSpace(x.Source)) { error = "game_id, api_name and source are required."; return false; }
        if (x.LastSeenAtUtc < x.FirstSeenAtUtc) { error = "last_seen_at_utc precedes first_seen_at_utc."; return false; }
        if (!x.IsUnlocked && (x.UnlockedAtUtc is not null || x.FirstUnlockedSeenAtUtc is not null)) { error = "Locked achievement contains unlock timestamps."; return false; }
        return true;
    }

    private static AchievementRow MergeAchievement(AchievementValue local, AchievementRow incoming)
    {
        var newer = incoming.LastSeenAtUtc >= local.LastSeenAtUtc;
        var unlocked = local.IsUnlocked || incoming.IsUnlocked;
        return incoming with
        {
            DisplayName = newer && !string.IsNullOrWhiteSpace(incoming.DisplayName) ? incoming.DisplayName : local.DisplayName,
            Description = newer && !string.IsNullOrWhiteSpace(incoming.Description) ? incoming.Description : local.Description,
            Hidden = newer ? incoming.Hidden : local.Hidden,
            IsUnlocked = unlocked,
            UnlockedAtUtc = unlocked ? Earliest(local.UnlockedAtUtc, incoming.UnlockedAtUtc) : null,
            Source = newer ? incoming.Source : local.Source,
            FirstSeenAtUtc = local.FirstSeenAtUtc <= incoming.FirstSeenAtUtc ? local.FirstSeenAtUtc : incoming.FirstSeenAtUtc,
            LastSeenAtUtc = local.LastSeenAtUtc >= incoming.LastSeenAtUtc ? local.LastSeenAtUtc : incoming.LastSeenAtUtc,
            FirstUnlockedSeenAtUtc = unlocked ? Earliest(local.FirstUnlockedSeenAtUtc, incoming.FirstUnlockedSeenAtUtc) : null
        };
    }

    private static bool AchievementEqual(AchievementValue a, AchievementRow b) => a.DisplayName == b.DisplayName && a.Description == b.Description && a.Hidden == b.Hidden && a.IsUnlocked == b.IsUnlocked && a.UnlockedAtUtc == b.UnlockedAtUtc && a.Source == b.Source && a.FirstSeenAtUtc == b.FirstSeenAtUtc && a.LastSeenAtUtc == b.LastSeenAtUtc && a.FirstUnlockedSeenAtUtc == b.FirstUnlockedSeenAtUtc;
    private static bool SessionEqual(SessionValue a, SessionValue b) => a.Domain.GameId == b.Domain.GameId && a.Domain.StartedAtUtc == b.Domain.StartedAtUtc && a.Domain.EndedAtUtc == b.Domain.EndedAtUtc && a.Domain.CaptureMethod == b.Domain.CaptureMethod && a.Domain.Confidence == b.Domain.Confidence && a.Domain.EndReason == b.Domain.EndReason && a.DurationMs == b.DurationMs;
    private static bool HistoryEqual(HistoryValue a, HistoryValue b) => a.Domain.GameId == b.Domain.GameId && a.Domain.Source == b.Domain.Source && a.Domain.Kind == b.Domain.Kind && a.Domain.Metric == b.Domain.Metric && a.Domain.Confidence == b.Domain.Confidence && a.Domain.PeriodStartUtc == b.Domain.PeriodStartUtc && a.Domain.PeriodEndUtc == b.Domain.PeriodEndUtc && a.DurationMs == b.DurationMs;
    private static bool OverlapsSession(SessionValue x, IEnumerable<SessionValue> items) => items.Any(y => x.Domain.GameId == y.Domain.GameId && Overlap(x.Domain.StartedAtUtc, x.Domain.EndedAtUtc, y.Domain.StartedAtUtc, y.Domain.EndedAtUtc));
    private static bool OverlapsHistory(HistoryValue x, IEnumerable<HistoryValue> items) => items.Any(y => x.Domain.GameId == y.Domain.GameId && Overlap(x.Domain.PeriodStartUtc, x.Domain.PeriodEndUtc, y.Domain.PeriodStartUtc, y.Domain.PeriodEndUtc));
    private static bool OverlapsMeasured(HistoryValue x, IEnumerable<SessionValue> items) => items.Any(y => x.Domain.GameId == y.Domain.GameId && Overlap(x.Domain.PeriodStartUtc, x.Domain.PeriodEndUtc, y.Domain.StartedAtUtc, y.Domain.EndedAtUtc));
    private static bool Overlap(DateTimeOffset a1, DateTimeOffset a2, DateTimeOffset b1, DateTimeOffset b2) => a1 < b2 && a2 > b1;
    private static DateTimeOffset? Earliest(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null ? a : a <= b ? a : b;
    private static string AchievementKey(Guid gameId, string api) => $"{gameId:D}|{(api ?? string.Empty).Trim().ToUpperInvariant()}";

    private static bool TryEnum<T>(string? value, out T result) where T : struct, Enum
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var compact = value.Replace("_", string.Empty, StringComparison.Ordinal);
        foreach (var name in Enum.GetNames<T>()) if (string.Equals(name, compact, StringComparison.OrdinalIgnoreCase)) return Enum.TryParse(name, out result);
        return false;
    }

    private static string NormalizeSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Import source path cannot be empty.", nameof(path));
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Portable export does not exist.", full);
        return full;
    }

    private static async Task<Document> ReadAsync(string path, CancellationToken token)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<Document>(stream, JsonOptions, token) ?? throw new InvalidDataException("Portable export is empty.");
        }
        catch (JsonException ex) { throw new InvalidDataException("The selected file is not valid GameHours portable JSON.", ex); }
    }

    private static async Task<DateTimeOffset?> ReadCutoverAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    {
        await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT tracking_started_at_utc FROM tracking_state WHERE singleton_id=1;";
        var v = await cmd.ExecuteScalarAsync(token); return v is null or DBNull ? null : Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!);
    }

    private static async Task<Dictionary<Guid, GameValue>> ReadGamesAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    {
        var d = new Dictionary<Guid, GameValue>(); await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT id,title,updated_at_utc FROM games;"; await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) { var x = new GameValue(Guid.Parse(r.GetString(0)), r.GetString(1), Parse(r.GetString(2))); d[x.Id] = x; } return d;
    }

    private static async Task<Dictionary<Guid, SessionValue>> ReadSessionsAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    {
        var d = new Dictionary<Guid, SessionValue>(); await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT id,game_id,started_at_utc,ended_at_utc,duration_ms,capture_method,confidence,end_reason FROM sessions;"; await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) { var s = new PlaySession(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Parse(r.GetString(2)), Parse(r.GetString(3)), (CaptureMethod)r.GetInt32(5), (Confidence)r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)); d[s.Id] = new(s, r.GetInt64(4)); } return d;
    }

    private static async Task<Dictionary<Guid, HistoryValue>> ReadHistoryAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    {
        var d = new Dictionary<Guid, HistoryValue>(); await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT id,game_id,source,evidence_kind,metric,confidence,period_start_utc,period_end_utc,duration_ms FROM historical_evidence;"; await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) { var h = new HistoricalEvidence(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), (HistoricalSource)r.GetInt32(2), (EvidenceKind)r.GetInt32(3), (PlaytimeMetric)r.GetInt32(4), (Confidence)r.GetInt32(5), Parse(r.GetString(6)), Parse(r.GetString(7)), TimeSpan.FromMilliseconds(r.GetInt64(8))); d[h.Id] = new(h, r.GetInt64(8)); } return d;
    }

    private static async Task<Dictionary<string, AchievementValue>> ReadAchievementsAsync(SqliteConnection c, SqliteTransaction t, CancellationToken token)
    {
        var d = new Dictionary<string, AchievementValue>(StringComparer.OrdinalIgnoreCase); await using var cmd = c.CreateCommand(); cmd.Transaction = t; cmd.CommandText = "SELECT game_id,api_name,display_name,description,hidden,is_unlocked,unlocked_at_utc,source,first_seen_at_utc,last_seen_at_utc,first_unlocked_seen_at_utc FROM achievement_states;"; await using var r = await cmd.ExecuteReaderAsync(token);
        while (await r.ReadAsync(token)) { var g = Guid.Parse(r.GetString(0)); var api = r.GetString(1); d[AchievementKey(g, api)] = new(g, api, r.GetString(2), r.GetString(3), r.GetInt64(4) != 0, r.GetInt64(5) != 0, r.IsDBNull(6) ? null : Parse(r.GetString(6)), r.GetString(7), Parse(r.GetString(8)), Parse(r.GetString(9)), r.IsDBNull(10) ? null : Parse(r.GetString(10))); } return d;
    }

    private static string Utc(DateTimeOffset x) => x.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTimeOffset Parse(string x) => DateTimeOffset.Parse(x, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true };

    private sealed record Plan(GameHoursPortableImportPreview Preview, DateTimeOffset? CutoverToSet, IReadOnlyList<GameRow> InsertGames, IReadOnlyList<GameRow> UpdateGames, IReadOnlyList<SessionValue> InsertSessions, IReadOnlyList<HistoryValue> InsertHistory, IReadOnlyList<AchievementRow> UpsertAchievements, IReadOnlyList<ObservationRow> UpsertObservations, IReadOnlyList<MilestoneRow> UpsertMilestones);
    private sealed record GameValue(Guid Id, string Title, DateTimeOffset UpdatedAtUtc);
    private sealed record SessionValue(PlaySession Domain, long DurationMs);
    private sealed record HistoryValue(HistoricalEvidence Domain, long DurationMs);
    private sealed record AchievementValue(Guid GameId, string ApiName, string DisplayName, string Description, bool Hidden, bool IsUnlocked, DateTimeOffset? UnlockedAtUtc, string Source, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? FirstUnlockedSeenAtUtc);

    private sealed class Document
    {
        public int FormatVersion { get; init; }
        public DateTimeOffset ExportedAtUtc { get; init; }
        public int SourceSchemaVersion { get; init; }
        public DateTimeOffset? TrackingStartedAtUtc { get; init; }
        public GameRow[]? Games { get; init; }
        public SessionRow[]? Sessions { get; init; }
        public HistoryRow[]? HistoricalEvidence { get; init; }
        public ObservationRow[]? AchievementObservations { get; init; }
        public AchievementRow[]? Achievements { get; init; }
        public MilestoneRow[]? AchievementCompletionMilestones { get; init; }
    }

    private sealed record GameRow(Guid Id, string Title, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc)
    {
        public GameRow Normalize() => this with { Title = (Title ?? string.Empty).Trim(), CreatedAtUtc = CreatedAtUtc.ToUniversalTime(), UpdatedAtUtc = UpdatedAtUtc.ToUniversalTime() };
    }
    private sealed record SessionRow(Guid Id, Guid GameId, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc, long DurationMilliseconds, string CaptureMethod, string Confidence, string? EndReason);
    private sealed record HistoryRow(Guid Id, Guid GameId, string Source, string EvidenceKind, string Metric, string Confidence, DateTimeOffset PeriodStartUtc, DateTimeOffset PeriodEndUtc, long DurationMilliseconds);
    private sealed record ObservationRow(Guid GameId, DateTimeOffset InitializedAtUtc, DateTimeOffset LastObservedAtUtc, string LastSource, bool HasCompleteCatalogue)
    {
        public ObservationRow Normalize() => this with { InitializedAtUtc = InitializedAtUtc.ToUniversalTime(), LastObservedAtUtc = LastObservedAtUtc.ToUniversalTime(), LastSource = (LastSource ?? string.Empty).Trim() };
    }
    private sealed record AchievementRow(Guid GameId, string ApiName, string DisplayName, string Description, bool Hidden, bool IsUnlocked, DateTimeOffset? UnlockedAtUtc, string Source, DateTimeOffset FirstSeenAtUtc, DateTimeOffset LastSeenAtUtc, DateTimeOffset? FirstUnlockedSeenAtUtc)
    {
        public AchievementRow Normalize() => this with { ApiName = (ApiName ?? string.Empty).Trim(), DisplayName = (DisplayName ?? string.Empty).Trim(), Description = (Description ?? string.Empty).Trim(), Source = (Source ?? string.Empty).Trim(), UnlockedAtUtc = UnlockedAtUtc?.ToUniversalTime(), FirstSeenAtUtc = FirstSeenAtUtc.ToUniversalTime(), LastSeenAtUtc = LastSeenAtUtc.ToUniversalTime(), FirstUnlockedSeenAtUtc = FirstUnlockedSeenAtUtc?.ToUniversalTime() };
    }
    private sealed record MilestoneRow(Guid GameId, DateTimeOffset CompletedAtUtc, bool IsObservedTimeFallback, string Source, DateTimeOffset RecordedAtUtc)
    {
        public MilestoneRow Normalize() => this with { CompletedAtUtc = CompletedAtUtc.ToUniversalTime(), Source = (Source ?? string.Empty).Trim(), RecordedAtUtc = RecordedAtUtc.ToUniversalTime() };
    }
}
