using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal sealed record DesktopMonthlyStatistics(
    DateOnly Month,
    TimeSpan MeasuredPlaytime,
    int ActiveDays,
    int GameCount,
    int AchievementCount,
    string? MostPlayedGameTitle,
    TimeSpan MostPlayedGameDuration,
    DateOnly? BusiestDay,
    TimeSpan BusiestDayDuration,
    TimeSpan AveragePerActiveDay);

internal sealed record DesktopLifetimeStatistics(
    TimeSpan KnownPlaytime,
    TimeSpan MeasuredPlaytime,
    TimeSpan HistoricalPlaytime,
    int GameCount,
    int SessionCount,
    int UnlockedAchievementCount,
    int CompletedGameCount,
    string? MostPlayedGameTitle,
    TimeSpan MostPlayedGameDuration,
    string? LongestSessionGameTitle,
    TimeSpan LongestSessionDuration,
    DateTimeOffset? FirstKnownActivityAtUtc,
    ActivityStreakSummary Streaks);

internal sealed record DesktopStatisticsSnapshot(
    DesktopMonthlyStatistics Month,
    DesktopLifetimeStatistics Lifetime);

/// <summary>
/// Builds presentation-neutral statistics from GameHours normalized SQLite data. Daily and
/// monthly playtime derives exclusively from measured sessions; historical evidence contributes
/// only to lifetime known-playtime totals because it cannot be safely distributed across days.
/// </summary>
internal sealed class DesktopStatisticsService
{
    private readonly SqliteGameRepository _games;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteHistoricalEvidenceRepository _historicalEvidence;
    private readonly SqliteAchievementActivityRepository _achievements;
    private readonly TimeZoneInfo _timeZone;

    public DesktopStatisticsService(
        string databasePath,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var database = new GameHoursDatabase(databasePath);
        _games = new SqliteGameRepository(database);
        _sessions = new SqliteSessionRepository(database);
        var trackingState = new SqliteTrackingStateRepository(database);
        _historicalEvidence = new SqliteHistoricalEvidenceRepository(
            database,
            trackingState,
            _sessions);
        _achievements = new SqliteAchievementActivityRepository(database);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public async Task<DesktopStatisticsSnapshot> LoadAsync(
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var monthFromUtc = PlaySessionDayAllocator.LocalMidnightToUtc(monthStart, _timeZone);
        var monthToUtc = PlaySessionDayAllocator.LocalMidnightToUtc(nextMonth, _timeZone);

        var games = await _games.GetAllAsync(cancellationToken);
        var unlocksTask = _achievements.GetUnlocksAsync(
            monthFromUtc,
            monthToUtc,
            cancellationToken: cancellationToken);

        var monthByGameTicks = new Dictionary<Guid, long>();
        var monthByDayTicks = new Dictionary<DateOnly, long>();
        var allActiveDates = new HashSet<DateOnly>();

        long measuredTicks = 0;
        long historicalTicks = 0;
        var sessionCount = 0;
        var gamesWithKnownActivity = 0;
        var unlockedAchievementCount = 0;
        var completedGameCount = 0;
        DateTimeOffset? firstKnownActivityAtUtc = null;
        string? lifetimeMostPlayedTitle = null;
        long lifetimeMostPlayedTicks = 0;
        string? longestSessionGameTitle = null;
        TimeSpan longestSessionDuration = TimeSpan.Zero;

        foreach (var game in games)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var sessionsTask = _sessions.GetForGameAsync(game.Id, cancellationToken: cancellationToken);
            var evidenceTask = _historicalEvidence.GetForGameAsync(game.Id, cancellationToken);
            var achievementsTask = _achievements.GetSummaryAsync(game.Id, cancellationToken);
            await Task.WhenAll(sessionsTask, evidenceTask, achievementsTask);

            var sessions = await sessionsTask;
            var evidence = await evidenceTask;
            var achievementSummary = await achievementsTask;

            long gameMeasuredTicks = 0;
            foreach (var session in sessions)
            {
                gameMeasuredTicks = checked(gameMeasuredTicks + session.Duration.Ticks);
                measuredTicks = checked(measuredTicks + session.Duration.Ticks);
                sessionCount++;

                if (firstKnownActivityAtUtc is null || session.StartedAtUtc < firstKnownActivityAtUtc.Value)
                {
                    firstKnownActivityAtUtc = session.StartedAtUtc;
                }

                if (session.Duration > longestSessionDuration)
                {
                    longestSessionDuration = session.Duration;
                    longestSessionGameTitle = game.Title;
                }

                foreach (var segment in PlaySessionDayAllocator.Split(session, _timeZone))
                {
                    allActiveDates.Add(segment.LocalDate);
                    if (segment.LocalDate < monthStart || segment.LocalDate >= nextMonth)
                    {
                        continue;
                    }

                    AddTicks(monthByGameTicks, game.Id, segment.Duration.Ticks);
                    AddTicks(monthByDayTicks, segment.LocalDate, segment.Duration.Ticks);
                }
            }

            long gameHistoricalTicks = 0;
            foreach (var item in evidence)
            {
                gameHistoricalTicks = checked(gameHistoricalTicks + item.Duration.Ticks);
                historicalTicks = checked(historicalTicks + item.Duration.Ticks);
                if (firstKnownActivityAtUtc is null || item.PeriodStartUtc < firstKnownActivityAtUtc.Value)
                {
                    firstKnownActivityAtUtc = item.PeriodStartUtc;
                }
            }

            var gameKnownTicks = checked(gameMeasuredTicks + gameHistoricalTicks);
            if (gameKnownTicks > 0 || achievementSummary is { UnlockedCount: > 0 })
            {
                gamesWithKnownActivity++;
            }

            if (gameKnownTicks > lifetimeMostPlayedTicks)
            {
                lifetimeMostPlayedTicks = gameKnownTicks;
                lifetimeMostPlayedTitle = game.Title;
            }

            if (achievementSummary is not null)
            {
                unlockedAchievementCount = checked(
                    unlockedAchievementCount + achievementSummary.UnlockedCount);
                if (achievementSummary.IsComplete)
                {
                    completedGameCount++;
                }
            }
        }

        var monthUnlocks = await unlocksTask;
        var monthMeasuredTicks = monthByDayTicks.Values.Aggregate(
            0L,
            (total, ticks) => checked(total + ticks));
        var activeDays = monthByDayTicks.Count(pair => pair.Value > 0);
        var monthGameCount = monthByGameTicks.Count(pair => pair.Value > 0);

        string? monthMostPlayedTitle = null;
        long monthMostPlayedTicks = 0;
        if (monthByGameTicks.Count > 0)
        {
            var best = monthByGameTicks
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => games.First(game => game.Id == pair.Key).Title, StringComparer.OrdinalIgnoreCase)
                .First();
            monthMostPlayedTicks = best.Value;
            monthMostPlayedTitle = games.First(game => game.Id == best.Key).Title;
        }

        DateOnly? busiestDay = null;
        long busiestDayTicks = 0;
        if (monthByDayTicks.Count > 0)
        {
            var best = monthByDayTicks
                .OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key)
                .First();
            busiestDay = best.Key;
            busiestDayTicks = best.Value;
        }

        var averageTicks = activeDays == 0
            ? 0L
            : monthMeasuredTicks / activeDays;
        var today = DateOnly.FromDateTime(
            TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).DateTime);
        var streaks = ActivityStreakCalculator.Calculate(allActiveDates, today);

        return new DesktopStatisticsSnapshot(
            new DesktopMonthlyStatistics(
                monthStart,
                TimeSpan.FromTicks(monthMeasuredTicks),
                activeDays,
                monthGameCount,
                monthUnlocks.Count,
                monthMostPlayedTitle,
                TimeSpan.FromTicks(monthMostPlayedTicks),
                busiestDay,
                TimeSpan.FromTicks(busiestDayTicks),
                TimeSpan.FromTicks(averageTicks)),
            new DesktopLifetimeStatistics(
                TimeSpan.FromTicks(checked(measuredTicks + historicalTicks)),
                TimeSpan.FromTicks(measuredTicks),
                TimeSpan.FromTicks(historicalTicks),
                gamesWithKnownActivity,
                sessionCount,
                unlockedAchievementCount,
                completedGameCount,
                lifetimeMostPlayedTitle,
                TimeSpan.FromTicks(lifetimeMostPlayedTicks),
                longestSessionGameTitle,
                longestSessionDuration,
                firstKnownActivityAtUtc,
                streaks));
    }

    private static void AddTicks<TKey>(Dictionary<TKey, long> target, TKey key, long ticks)
        where TKey : notnull
    {
        target.TryGetValue(key, out var current);
        target[key] = checked(current + ticks);
    }
}
