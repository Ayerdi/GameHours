using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal sealed record DesktopGameInsight(
    string HistoricalSourceText,
    string HistoricalCoverageText,
    string FirstAchievementText,
    string LastAchievementText,
    string AchievementProgressText,
    string ActivitySummaryText,
    TimeSpan? FocusedPlaytime,
    TimeSpan? ActivePlaytime,
    TimeSpan? AfkPlaytime,
    int ActivitySessionCount,
    int AfkEstimatedSessionCount,
    int MeasuredSessionCount,
    IReadOnlyList<DesktopTimelineRow> RecentActivity);

internal sealed class DesktopGameInsightService
{
    private const int RecentActivityLimit = 20;

    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteSessionActivityRepository _sessionActivity;
    private readonly SqliteHistoricalEvidenceRepository _historicalEvidence;
    private readonly SqliteAchievementActivityRepository _achievementActivity;

    public DesktopGameInsightService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var database = new GameHoursDatabase(databasePath);
        _sessions = new SqliteSessionRepository(database);
        _sessionActivity = new SqliteSessionActivityRepository(database);
        var trackingState = new SqliteTrackingStateRepository(database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(database, trackingState, _sessions);
        _achievementActivity = new SqliteAchievementActivityRepository(database);
    }

    public async Task<DesktopGameInsight> LoadAsync(
        Guid gameId,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        }

        var evidenceTask = _historicalEvidence.GetForGameAsync(gameId, cancellationToken);
        var achievementSummaryTask = _achievementActivity.GetSummaryAsync(gameId, cancellationToken);
        var sessionsTask = _sessions.GetForGameAsync(gameId, cancellationToken: cancellationToken);
        var sessionActivityTask = _sessionActivity.GetForGameAsync(gameId, cancellationToken);
        var unlocksTask = _achievementActivity.GetRecentUnlocksAsync(RecentActivityLimit, gameId, cancellationToken);
        var completionsTask = _achievementActivity.GetRecentCompletionMilestonesAsync(RecentActivityLimit, gameId, cancellationToken);

        await Task.WhenAll(
            evidenceTask,
            achievementSummaryTask,
            sessionsTask,
            sessionActivityTask,
            unlocksTask,
            completionsTask);

        var evidence = await evidenceTask;
        var historical = HistoricalCoverageSummarizer.Build(gameId, evidence);
        var achievements = await achievementSummaryTask;
        var sessions = await sessionsTask;
        var finalizedActivity = (await sessionActivityTask)
            .Where(item => item.IsFinalized)
            .ToArray();
        var activityBySession = finalizedActivity.ToDictionary(item => item.SessionId);
        var afkEstimatedActivity = finalizedActivity
            .Where(item => item.AfkFilterEnabled)
            .ToArray();

        var focusedTicks = finalizedActivity.Aggregate(
            0L,
            (total, item) => checked(total + item.FocusedDuration.Ticks));
        var activeTicks = afkEstimatedActivity.Aggregate(
            0L,
            (total, item) => checked(total + item.ActiveDuration.Ticks));
        var afkFocusedTicks = afkEstimatedActivity.Aggregate(
            0L,
            (total, item) => checked(total + item.FocusedDuration.Ticks));
        var afkTicks = Math.Max(0L, afkFocusedTicks - activeTicks);

        var recentActivity = BuildRecentActivity(
            sessions,
            activityBySession,
            await unlocksTask,
            await completionsTask);

        return new DesktopGameInsight(
            HistoricalSourceText(historical),
            HistoricalCoverageText(historical),
            FormatAchievementDate(achievements?.FirstUnlockedAtUtc),
            FormatAchievementDate(achievements?.LastUnlockedAtUtc),
            AchievementProgressText(achievements),
            ActivitySummaryText(recentActivity),
            finalizedActivity.Length == 0 ? null : TimeSpan.FromTicks(focusedTicks),
            afkEstimatedActivity.Length == 0 ? null : TimeSpan.FromTicks(activeTicks),
            afkEstimatedActivity.Length == 0 ? null : TimeSpan.FromTicks(afkTicks),
            finalizedActivity.Length,
            afkEstimatedActivity.Length,
            sessions.Count,
            recentActivity);
    }

    private static IReadOnlyList<DesktopTimelineRow> BuildRecentActivity(
        IReadOnlyList<PlaySession> sessions,
        IReadOnlyDictionary<Guid, SessionActivityMetrics> activityBySession,
        IReadOnlyList<AchievementUnlockActivity> unlocks,
        IReadOnlyList<AchievementCompletionMilestone> completions)
    {
        return sessions
            .OrderByDescending(session => session.EndedAtUtc)
            .Take(RecentActivityLimit)
            .Select(session =>
            {
                activityBySession.TryGetValue(session.Id, out var attention);
                return new DesktopTimelineRow(
                    session.GameId,
                    string.Empty,
                    session.EndedAtUtc,
                    DesktopTimelineKind.Session,
                    Duration: session.Duration,
                    EndReason: session.EndReason,
                    FocusedDuration: attention?.FocusedDuration,
                    ActiveDuration: attention is { AfkFilterEnabled: true }
                        ? attention.ActiveDuration
                        : null,
                    SessionId: session.Id);
            })
            .Concat(unlocks.Select(unlock => new DesktopTimelineRow(
                unlock.GameId,
                unlock.GameTitle,
                unlock.OccurredAtUtc,
                DesktopTimelineKind.AchievementUnlocked,
                AchievementApiName: unlock.ApiName,
                AchievementDisplayName: AchievementPresentation.TimelineText(unlock.DisplayName, unlock.ApiName, unlock.Description),
                IsObservedTimeFallback: unlock.IsObservedTimeFallback)))
            .Concat(completions.Select(completion => new DesktopTimelineRow(
                completion.GameId,
                completion.GameTitle,
                completion.CompletedAtUtc,
                DesktopTimelineKind.AchievementCompleted,
                AchievementDisplayName: "100 % completado",
                IsObservedTimeFallback: completion.IsObservedTimeFallback)))
            .OrderByDescending(item => item.OccurredAtUtc)
            .ThenBy(item => item.Kind)
            .Take(RecentActivityLimit)
            .ToArray();
    }

    private static string ActivitySummaryText(IReadOnlyList<DesktopTimelineRow> activity)
    {
        if (activity.Count == 0)
        {
            return "Todavía no hay sesiones, logros o hitos persistidos para este juego.";
        }

        var sessionCount = activity.Count(item => item.Kind == DesktopTimelineKind.Session);
        var achievementCount = activity.Count(item => item.Kind == DesktopTimelineKind.AchievementUnlocked);
        var completionCount = activity.Count(item => item.Kind == DesktopTimelineKind.AchievementCompleted);
        var eventLabel = activity.Count == 1 ? "1 evento reciente" : $"{activity.Count} eventos recientes";
        var sessionLabel = sessionCount == 1 ? "1 sesión" : $"{sessionCount} sesiones";
        var achievementLabel = achievementCount == 1 ? "1 logro" : $"{achievementCount} logros";
        var summary = $"{eventLabel} · {sessionLabel} · {achievementLabel}";
        return completionCount switch
        {
            0 => summary + ".",
            1 => summary + " · 1 hito al 100 %.",
            _ => summary + $" · {completionCount} hitos al 100 %."
        };
    }

    private static string HistoricalSourceText(HistoricalCoverageSummary? summary)
    {
        if (summary is null) return "Sin histórico recuperado";
        return string.Join(" · ", summary.Sources.Select(source => SourceName(source.Source)));
    }

    private static string HistoricalCoverageText(HistoricalCoverageSummary? summary)
    {
        if (summary is null) return "No hay una ventana de evidencia histórica guardada.";
        var first = summary.FirstKnownActivityAtUtc.ToLocalTime();
        var last = summary.LastKnownActivityAtUtc.ToLocalTime();
        return $"Evidencia guardada: {FormatCompactDate(first)} – {FormatCompactDate(last)}";
    }

    private static string AchievementProgressText(AchievementGameSummary? summary)
    {
        if (summary is null) return "Sin datos persistidos";
        if (summary.IsComplete) return "100 % completado";
        if (summary.HasCompleteCatalogue && summary.KnownCount > 0)
        {
            return $"{summary.UnlockedCount}/{summary.KnownCount} · {summary.CompletionPercentage ?? 0d:0}%";
        }
        return summary.UnlockedCount == 1
            ? "1 desbloqueado · total desconocido"
            : $"{summary.UnlockedCount} desbloqueados · total desconocido";
    }

    private static string FormatAchievementDate(DateTimeOffset? value) =>
        value is null ? "—" : FormatCompactDate(value.Value.ToLocalTime(), includeTime: true);

    private static string FormatCompactDate(DateTimeOffset value, bool includeTime = false)
    {
        var today = DateTimeOffset.Now.Date;
        string date = value.Date == today
            ? "Hoy"
            : value.Date == today.AddDays(-1)
                ? "Ayer"
                : value.Year == DateTimeOffset.Now.Year
                    ? value.ToString("dd MMM")
                    : value.ToString("dd/MM/yy");
        return includeTime ? $"{date} · {value:HH:mm}" : date;
    }

    private static string SourceName(HistoricalSource source) => source switch
    {
        HistoricalSource.Srum => "SRUM",
        HistoricalSource.UserAssist => "UserAssist",
        HistoricalSource.ManualImport => "Importación manual",
        _ => source.ToString()
    };
}
