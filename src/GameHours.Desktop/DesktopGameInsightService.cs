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
    IReadOnlyList<DesktopTimelineRow> RecentActivity);

/// <summary>
/// Builds presentation-ready, read-only summaries from GameHours' normalized SQLite data.
/// Confidence remains part of the underlying historical model, but is deliberately not
/// exposed as a normal user-facing label here.
/// </summary>
internal sealed class DesktopGameInsightService
{
    private const int RecentActivityLimit = 20;

    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteHistoricalEvidenceRepository _historicalEvidence;
    private readonly SqliteAchievementActivityRepository _achievementActivity;

    public DesktopGameInsightService(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var database = new GameHoursDatabase(databasePath);
        _sessions = new SqliteSessionRepository(database);
        var trackingState = new SqliteTrackingStateRepository(database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(
            database,
            trackingState,
            _sessions);
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
        var unlocksTask = _achievementActivity.GetRecentUnlocksAsync(
            limit: RecentActivityLimit,
            gameId: gameId,
            cancellationToken: cancellationToken);
        var completionsTask = _achievementActivity.GetRecentCompletionMilestonesAsync(
            limit: RecentActivityLimit,
            gameId: gameId,
            cancellationToken: cancellationToken);

        await Task.WhenAll(
            evidenceTask,
            achievementSummaryTask,
            sessionsTask,
            unlocksTask,
            completionsTask);

        var evidence = await evidenceTask;
        var historical = HistoricalCoverageSummarizer.Build(gameId, evidence);
        var achievements = await achievementSummaryTask;
        var sessions = await sessionsTask;
        var unlocks = await unlocksTask;
        var completions = await completionsTask;
        var recentActivity = BuildRecentActivity(sessions, unlocks, completions);

        return new DesktopGameInsight(
            HistoricalSourceText(historical),
            HistoricalCoverageText(historical),
            FormatAchievementDate(achievements?.FirstUnlockedAtUtc),
            FormatAchievementDate(achievements?.LastUnlockedAtUtc),
            AchievementProgressText(achievements),
            ActivitySummaryText(recentActivity),
            recentActivity);
    }

    private static IReadOnlyList<DesktopTimelineRow> BuildRecentActivity(
        IReadOnlyList<PlaySession> sessions,
        IReadOnlyList<AchievementUnlockActivity> unlocks,
        IReadOnlyList<AchievementCompletionMilestone> completions)
    {
        return sessions
            .OrderByDescending(session => session.EndedAtUtc)
            .Take(RecentActivityLimit)
            .Select(session => new DesktopTimelineRow(
                session.GameId,
                string.Empty,
                session.EndedAtUtc,
                DesktopTimelineKind.Session,
                Duration: session.Duration,
                EndReason: session.EndReason))
            .Concat(unlocks.Select(unlock => new DesktopTimelineRow(
                unlock.GameId,
                unlock.GameTitle,
                unlock.OccurredAtUtc,
                DesktopTimelineKind.AchievementUnlocked,
                AchievementApiName: unlock.ApiName,
                AchievementDisplayName: AchievementPresentation.TimelineText(
                    unlock.DisplayName,
                    unlock.ApiName,
                    unlock.Description),
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
        if (summary is null)
        {
            return "Sin histórico recuperado";
        }

        return string.Join(
            " · ",
            summary.Sources.Select(source => SourceName(source.Source)));
    }

    private static string HistoricalCoverageText(HistoricalCoverageSummary? summary)
    {
        if (summary is null)
        {
            return "No hay una ventana de evidencia histórica guardada.";
        }

        var first = summary.FirstKnownActivityAtUtc.ToLocalTime();
        var last = summary.LastKnownActivityAtUtc.ToLocalTime();
        return $"Evidencia guardada: {FormatCompactDate(first)} – {FormatCompactDate(last)}";
    }

    private static string AchievementProgressText(AchievementGameSummary? summary)
    {
        if (summary is null)
        {
            return "Sin datos persistidos";
        }

        if (summary.IsComplete)
        {
            return "100 % completado";
        }

        if (summary.HasCompleteCatalogue && summary.KnownCount > 0)
        {
            var percentage = summary.CompletionPercentage ?? 0d;
            return $"{summary.UnlockedCount}/{summary.KnownCount} · {percentage:0}%";
        }

        return summary.UnlockedCount == 1
            ? "1 desbloqueado · total desconocido"
            : $"{summary.UnlockedCount} desbloqueados · total desconocido";
    }

    private static string FormatAchievementDate(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "—";
        }

        return FormatCompactDate(value.Value.ToLocalTime(), includeTime: true);
    }

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
