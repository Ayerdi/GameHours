using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal sealed record DesktopMonthlyStatistics(DateOnly Month, TimeSpan MeasuredPlaytime, int ActiveDays, int GameCount, int AchievementCount, string? MostPlayedGameTitle, TimeSpan MostPlayedGameDuration, DateOnly? BusiestDay, TimeSpan BusiestDayDuration, TimeSpan AveragePerActiveDay);
internal sealed record DesktopLifetimeStatistics(TimeSpan KnownPlaytime, TimeSpan MeasuredPlaytime, TimeSpan HistoricalPlaytime, int GameCount, int SessionCount, int UnlockedAchievementCount, int CompletedGameCount, string? MostPlayedGameTitle, TimeSpan MostPlayedGameDuration, string? LongestSessionGameTitle, TimeSpan LongestSessionDuration, DateTimeOffset? FirstKnownActivityAtUtc, ActivityStreakSummary Streaks);
internal sealed record DesktopStatisticsSnapshot(DesktopMonthlyStatistics Month, DesktopLifetimeStatistics Lifetime);

internal sealed class DesktopStatisticsService
{
    private readonly SqliteGameRepository _games;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteHistoricalEvidenceRepository _historicalEvidence;
    private readonly SqliteAchievementActivityRepository _achievementActivity;
    private readonly SqliteAchievementSummaryRepository _achievementSummaries;
    private readonly TimeZoneInfo _timeZone;

    public DesktopStatisticsService(string databasePath, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var database = new GameHoursDatabase(databasePath);
        _games = new(database);
        _sessions = new(database);
        var tracking = new SqliteTrackingStateRepository(database);
        _historicalEvidence = new(database, tracking, _sessions);
        _achievementActivity = new(database);
        _achievementSummaries = new(database);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public async Task<DesktopStatisticsSnapshot> LoadAsync(DateOnly month, CancellationToken cancellationToken = default)
    {
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var fromUtc = PlaySessionDayAllocator.LocalMidnightToUtc(monthStart, _timeZone);
        var toUtc = PlaySessionDayAllocator.LocalMidnightToUtc(nextMonth, _timeZone);
        var gamesTask = _games.GetAllAsync(cancellationToken);
        var sessionsTask = _sessions.GetAllAsync(cancellationToken: cancellationToken);
        var evidenceTask = _historicalEvidence.GetAllAsync(cancellationToken);
        var summariesTask = _achievementSummaries.GetAllAsync(cancellationToken);
        var monthUnlocksTask = _achievementActivity.GetUnlocksAsync(fromUtc, toUtc, cancellationToken: cancellationToken);
        await Task.WhenAll(gamesTask, sessionsTask, evidenceTask, summariesTask, monthUnlocksTask);

        var games = await gamesTask;
        var titles = games.ToDictionary(game => game.Id, game => game.Title);
        var sessions = await sessionsTask;
        var evidence = await evidenceTask;
        var summaries = await summariesTask;
        var measuredByGame = new Dictionary<Guid, long>();
        var historicalByGame = new Dictionary<Guid, long>();
        var monthByGame = new Dictionary<Guid, long>();
        var monthByDay = new Dictionary<DateOnly, long>();
        var allActiveDates = new HashSet<DateOnly>();
        long measuredTicks = 0;
        DateTimeOffset? firstKnown = null;
        string? longestTitle = null;
        var longestDuration = TimeSpan.Zero;

        foreach (var session in sessions)
        {
            measuredTicks = checked(measuredTicks + session.Duration.Ticks);
            AddTicks(measuredByGame, session.GameId, session.Duration.Ticks);
            if (firstKnown is null || session.StartedAtUtc < firstKnown) firstKnown = session.StartedAtUtc;
            if (session.Duration > longestDuration) { longestDuration = session.Duration; titles.TryGetValue(session.GameId, out longestTitle); }
            foreach (var segment in PlaySessionDayAllocator.Split(session, _timeZone))
            {
                allActiveDates.Add(segment.LocalDate);
                if (segment.LocalDate < monthStart || segment.LocalDate >= nextMonth) continue;
                AddTicks(monthByGame, session.GameId, segment.Duration.Ticks);
                AddTicks(monthByDay, segment.LocalDate, segment.Duration.Ticks);
            }
        }

        long historicalTicks = 0;
        foreach (var item in evidence)
        {
            historicalTicks = checked(historicalTicks + item.Duration.Ticks);
            AddTicks(historicalByGame, item.GameId, item.Duration.Ticks);
            if (firstKnown is null || item.PeriodStartUtc < firstKnown) firstKnown = item.PeriodStartUtc;
        }

        var knownGames = games.Select(game => new
        {
            Game = game,
            KnownTicks = measuredByGame.GetValueOrDefault(game.Id) + historicalByGame.GetValueOrDefault(game.Id),
            Summary = summaries.GetValueOrDefault(game.Id)
        }).ToArray();
        var gamesWithKnownActivity = knownGames.Count(item => item.KnownTicks > 0 || item.Summary is { UnlockedCount: > 0 });
        var lifetimeBest = knownGames.OrderByDescending(item => item.KnownTicks).ThenBy(item => item.Game.Title, StringComparer.OrdinalIgnoreCase).FirstOrDefault(item => item.KnownTicks > 0);
        var monthBest = monthByGame.OrderByDescending(item => item.Value).ThenBy(item => titles.GetValueOrDefault(item.Key), StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        var busiest = monthByDay.OrderByDescending(item => item.Value).ThenBy(item => item.Key).FirstOrDefault();
        var monthTicks = monthByDay.Values.Sum();
        var activeDays = monthByDay.Count(item => item.Value > 0);
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).DateTime);

        return new DesktopStatisticsSnapshot(
            new DesktopMonthlyStatistics(
                monthStart,
                TimeSpan.FromTicks(monthTicks),
                activeDays,
                monthByGame.Count(item => item.Value > 0),
                (await monthUnlocksTask).Count,
                monthBest.Key == Guid.Empty ? null : titles.GetValueOrDefault(monthBest.Key),
                TimeSpan.FromTicks(monthBest.Value),
                busiest.Key == default ? null : busiest.Key,
                TimeSpan.FromTicks(busiest.Value),
                TimeSpan.FromTicks(activeDays == 0 ? 0 : monthTicks / activeDays)),
            new DesktopLifetimeStatistics(
                TimeSpan.FromTicks(checked(measuredTicks + historicalTicks)),
                TimeSpan.FromTicks(measuredTicks),
                TimeSpan.FromTicks(historicalTicks),
                gamesWithKnownActivity,
                sessions.Count,
                summaries.Values.Sum(item => item.UnlockedCount),
                summaries.Values.Count(item => item.IsComplete),
                lifetimeBest?.Game.Title,
                TimeSpan.FromTicks(lifetimeBest?.KnownTicks ?? 0),
                longestTitle,
                longestDuration,
                firstKnown,
                ActivityStreakCalculator.Calculate(allActiveDates, today)));
    }

    private static void AddTicks<TKey>(Dictionary<TKey, long> target, TKey key, long ticks) where TKey : notnull => target[key] = checked(target.GetValueOrDefault(key) + ticks);
}
