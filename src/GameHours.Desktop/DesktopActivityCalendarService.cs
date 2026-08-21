using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal enum DesktopCalendarEventKind
{
    Session = 1,
    AchievementUnlocked = 2,
    AchievementCompleted = 3
}

internal sealed record DesktopCalendarEvent(
    DateOnly LocalDate,
    DateTimeOffset OccurredAtUtc,
    Guid GameId,
    string GameTitle,
    DesktopCalendarEventKind Kind,
    TimeSpan? Duration = null,
    string? Title = null,
    string? Description = null,
    string? EndReason = null,
    bool IsObservedTimeFallback = false,
    bool StartedBeforeLocalDay = false,
    bool ContinuesAfterLocalDay = false);

internal sealed record DesktopCalendarDay(
    DateOnly Date,
    TimeSpan MeasuredPlaytime,
    int AchievementCount,
    int CompletionCount,
    int GameCount,
    IReadOnlyList<DesktopCalendarEvent> Events);

internal sealed record DesktopCalendarMonth(
    DateOnly Month,
    TimeSpan MeasuredPlaytime,
    int AchievementCount,
    int CompletionCount,
    int GameCount,
    TimeSpan BusiestDayPlaytime,
    IReadOnlyList<DesktopCalendarDay> Days);

/// <summary>
/// Builds a month/day diary exclusively from normalized GameHours persistence. Historical
/// evidence such as SRUM is deliberately excluded because it cannot be distributed across
/// individual calendar days with measured-session precision. Achievement timestamps and safe
/// 100%-completion milestones can still reconstruct historical diary events when their source
/// provides a real occurrence time.
/// </summary>
internal sealed class DesktopActivityCalendarService
{
    private readonly SqliteGameRepository _games;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteAchievementActivityRepository _achievements;
    private readonly TimeZoneInfo _timeZone;

    public DesktopActivityCalendarService(
        string databasePath,
        TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var database = new GameHoursDatabase(databasePath);
        _games = new SqliteGameRepository(database);
        _sessions = new SqliteSessionRepository(database);
        _achievements = new SqliteAchievementActivityRepository(database);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public async Task<DesktopCalendarMonth> LoadMonthAsync(
        DateOnly month,
        CancellationToken cancellationToken = default)
    {
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var fromUtc = PlaySessionDayAllocator.LocalMidnightToUtc(monthStart, _timeZone);
        var toUtc = PlaySessionDayAllocator.LocalMidnightToUtc(nextMonth, _timeZone);

        var games = await _games.GetAllAsync(cancellationToken);
        var sessionTasks = games.Select(async game => new
        {
            Game = game,
            Sessions = await _sessions.GetForGameAsync(
                game.Id,
                fromUtc,
                toUtc,
                cancellationToken)
        }).ToArray();
        var unlocksTask = _achievements.GetUnlocksAsync(
            fromUtc,
            toUtc,
            cancellationToken: cancellationToken);
        var completionsTask = _achievements.GetCompletionMilestonesAsync(
            fromUtc,
            toUtc,
            cancellationToken: cancellationToken);

        await Task.WhenAll(sessionTasks);
        var unlocks = await unlocksTask;
        var completions = await completionsTask;

        var builders = Enumerable.Range(0, nextMonth.DayNumber - monthStart.DayNumber)
            .Select(offset => monthStart.AddDays(offset))
            .ToDictionary(date => date, date => new DayBuilder(date));

        foreach (var task in sessionTasks)
        {
            var item = await task;
            foreach (var session in item.Sessions)
            {
                foreach (var segment in PlaySessionDayAllocator.Split(session, _timeZone))
                {
                    if (!builders.TryGetValue(segment.LocalDate, out var day))
                    {
                        continue;
                    }

                    day.AddPlaytime(segment.Duration, session.GameId);
                    var dayStartUtc = PlaySessionDayAllocator.LocalMidnightToUtc(segment.LocalDate, _timeZone);
                    var dayEndUtc = PlaySessionDayAllocator.LocalMidnightToUtc(segment.LocalDate.AddDays(1), _timeZone);
                    day.Events.Add(new DesktopCalendarEvent(
                        segment.LocalDate,
                        segment.StartedAtUtc,
                        session.GameId,
                        item.Game.Title,
                        DesktopCalendarEventKind.Session,
                        Duration: segment.Duration,
                        EndReason: session.EndReason,
                        StartedBeforeLocalDay: session.StartedAtUtc < dayStartUtc,
                        ContinuesAfterLocalDay: session.EndedAtUtc > dayEndUtc));
                }
            }
        }

        foreach (var unlock in unlocks)
        {
            var local = TimeZoneInfo.ConvertTime(unlock.OccurredAtUtc, _timeZone);
            var date = DateOnly.FromDateTime(local.DateTime);
            if (!builders.TryGetValue(date, out var day))
            {
                continue;
            }

            day.AddAchievement(unlock.GameId);
            day.Events.Add(new DesktopCalendarEvent(
                date,
                unlock.OccurredAtUtc,
                unlock.GameId,
                unlock.GameTitle,
                DesktopCalendarEventKind.AchievementUnlocked,
                Title: string.IsNullOrWhiteSpace(unlock.DisplayName) ? unlock.ApiName : unlock.DisplayName,
                Description: string.IsNullOrWhiteSpace(unlock.Description) ? null : unlock.Description,
                IsObservedTimeFallback: unlock.IsObservedTimeFallback));
        }

        foreach (var completion in completions)
        {
            var local = TimeZoneInfo.ConvertTime(completion.CompletedAtUtc, _timeZone);
            var date = DateOnly.FromDateTime(local.DateTime);
            if (!builders.TryGetValue(date, out var day))
            {
                continue;
            }

            day.AddCompletion(completion.GameId);
            day.Events.Add(new DesktopCalendarEvent(
                date,
                completion.CompletedAtUtc,
                completion.GameId,
                completion.GameTitle,
                DesktopCalendarEventKind.AchievementCompleted,
                Title: "100 % completado",
                Description: "Todos los logros del catálogo conocido están desbloqueados.",
                IsObservedTimeFallback: completion.IsObservedTimeFallback));
        }

        var days = builders.Values
            .OrderBy(day => day.Date)
            .Select(day => day.Build())
            .ToArray();
        var totalTicks = days.Aggregate(
            0L,
            (total, day) => checked(total + day.MeasuredPlaytime.Ticks));
        var achievementCount = days.Sum(day => day.AchievementCount);
        var completionCount = days.Sum(day => day.CompletionCount);
        var gameCount = days
            .SelectMany(day => day.Events.Select(item => item.GameId))
            .Distinct()
            .Count();
        var busiestTicks = days.Count == 0
            ? 0L
            : days.Max(day => day.MeasuredPlaytime.Ticks);

        return new DesktopCalendarMonth(
            monthStart,
            TimeSpan.FromTicks(totalTicks),
            achievementCount,
            completionCount,
            gameCount,
            TimeSpan.FromTicks(busiestTicks),
            days);
    }

    private sealed class DayBuilder
    {
        private long _playtimeTicks;
        private readonly HashSet<Guid> _games = new();

        public DayBuilder(DateOnly date)
        {
            Date = date;
        }

        public DateOnly Date { get; }
        public int AchievementCount { get; private set; }
        public int CompletionCount { get; private set; }
        public List<DesktopCalendarEvent> Events { get; } = new();

        public void AddPlaytime(TimeSpan duration, Guid gameId)
        {
            _playtimeTicks = checked(_playtimeTicks + duration.Ticks);
            _games.Add(gameId);
        }

        public void AddAchievement(Guid gameId)
        {
            AchievementCount++;
            _games.Add(gameId);
        }

        public void AddCompletion(Guid gameId)
        {
            CompletionCount++;
            _games.Add(gameId);
        }

        public DesktopCalendarDay Build() => new(
            Date,
            TimeSpan.FromTicks(_playtimeTicks),
            AchievementCount,
            CompletionCount,
            _games.Count,
            Events
                .OrderBy(item => item.OccurredAtUtc)
                .ThenBy(item => item.Kind)
                .ToArray());
    }
}
