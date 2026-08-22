using GameHours.Core.Timeline;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal enum DesktopCalendarEventKind { Session = 1, AchievementUnlocked = 2, AchievementCompleted = 3 }
internal sealed record DesktopCalendarEvent(DateOnly LocalDate, DateTimeOffset OccurredAtUtc, Guid GameId, string GameTitle, DesktopCalendarEventKind Kind, TimeSpan? Duration = null, string? Title = null, string? Description = null, string? EndReason = null, bool IsObservedTimeFallback = false, bool StartedBeforeLocalDay = false, bool ContinuesAfterLocalDay = false);
internal sealed record DesktopCalendarDay(DateOnly Date, TimeSpan MeasuredPlaytime, int AchievementCount, int CompletionCount, int GameCount, IReadOnlyList<DesktopCalendarEvent> Events);
internal sealed record DesktopCalendarMonth(DateOnly Month, TimeSpan MeasuredPlaytime, int AchievementCount, int CompletionCount, int GameCount, TimeSpan BusiestDayPlaytime, IReadOnlyList<DesktopCalendarDay> Days);

internal sealed class DesktopActivityCalendarService
{
    private readonly SqliteGameRepository _games;
    private readonly SqliteSessionRepository _sessions;
    private readonly SqliteAchievementActivityRepository _achievements;
    private readonly TimeZoneInfo _timeZone;

    public DesktopActivityCalendarService(string databasePath, TimeZoneInfo? timeZone = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var database = new GameHoursDatabase(databasePath);
        _games = new(database);
        _sessions = new(database);
        _achievements = new(database);
        _timeZone = timeZone ?? TimeZoneInfo.Local;
    }

    public async Task<DesktopCalendarMonth> LoadMonthAsync(DateOnly month, CancellationToken cancellationToken = default)
    {
        var monthStart = new DateOnly(month.Year, month.Month, 1);
        var nextMonth = monthStart.AddMonths(1);
        var fromUtc = PlaySessionDayAllocator.LocalMidnightToUtc(monthStart, _timeZone);
        var toUtc = PlaySessionDayAllocator.LocalMidnightToUtc(nextMonth, _timeZone);
        var gamesTask = _games.GetAllAsync(cancellationToken);
        var sessionsTask = _sessions.GetAllAsync(fromUtc, toUtc, cancellationToken);
        var unlocksTask = _achievements.GetUnlocksAsync(fromUtc, toUtc, cancellationToken: cancellationToken);
        var completionsTask = _achievements.GetCompletionMilestonesAsync(fromUtc, toUtc, cancellationToken: cancellationToken);
        await Task.WhenAll(gamesTask, sessionsTask, unlocksTask, completionsTask);
        var titles = (await gamesTask).ToDictionary(game => game.Id, game => game.Title);
        var builders = Enumerable.Range(0, nextMonth.DayNumber - monthStart.DayNumber).Select(monthStart.AddDays).ToDictionary(date => date, date => new DayBuilder(date));

        foreach (var session in await sessionsTask)
        {
            if (!titles.TryGetValue(session.GameId, out var title)) continue;
            foreach (var segment in PlaySessionDayAllocator.Split(session, _timeZone))
            {
                if (!builders.TryGetValue(segment.LocalDate, out var day)) continue;
                day.AddPlaytime(segment.Duration, session.GameId);
                var dayStart = PlaySessionDayAllocator.LocalMidnightToUtc(segment.LocalDate, _timeZone);
                var dayEnd = PlaySessionDayAllocator.LocalMidnightToUtc(segment.LocalDate.AddDays(1), _timeZone);
                day.Events.Add(new(segment.LocalDate, segment.StartedAtUtc, session.GameId, title, DesktopCalendarEventKind.Session, segment.Duration, EndReason: session.EndReason, StartedBeforeLocalDay: session.StartedAtUtc < dayStart, ContinuesAfterLocalDay: session.EndedAtUtc > dayEnd));
            }
        }

        foreach (var unlock in await unlocksTask)
        {
            var date = LocalDate(unlock.OccurredAtUtc);
            if (!builders.TryGetValue(date, out var day)) continue;
            day.AddAchievement(unlock.GameId);
            day.Events.Add(new(date, unlock.OccurredAtUtc, unlock.GameId, unlock.GameTitle, DesktopCalendarEventKind.AchievementUnlocked, Title: string.IsNullOrWhiteSpace(unlock.DisplayName) ? unlock.ApiName : unlock.DisplayName, Description: string.IsNullOrWhiteSpace(unlock.Description) ? null : unlock.Description, IsObservedTimeFallback: unlock.IsObservedTimeFallback));
        }

        foreach (var completion in await completionsTask)
        {
            var date = LocalDate(completion.CompletedAtUtc);
            if (!builders.TryGetValue(date, out var day)) continue;
            day.AddCompletion(completion.GameId);
            day.Events.Add(new(date, completion.CompletedAtUtc, completion.GameId, completion.GameTitle, DesktopCalendarEventKind.AchievementCompleted, Title: "100 % completado", Description: "Todos los logros del catálogo conocido están desbloqueados.", IsObservedTimeFallback: completion.IsObservedTimeFallback));
        }

        var days = builders.Values.OrderBy(day => day.Date).Select(day => day.Build()).ToArray();
        return new DesktopCalendarMonth(
            monthStart,
            TimeSpan.FromTicks(days.Sum(day => day.MeasuredPlaytime.Ticks)),
            days.Sum(day => day.AchievementCount),
            days.Sum(day => day.CompletionCount),
            days.SelectMany(day => day.Events.Select(item => item.GameId)).Distinct().Count(),
            TimeSpan.FromTicks(days.Length == 0 ? 0 : days.Max(day => day.MeasuredPlaytime.Ticks)),
            days);
    }

    private DateOnly LocalDate(DateTimeOffset utc) => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(utc, _timeZone).DateTime);

    private sealed class DayBuilder(DateOnly date)
    {
        private long _ticks;
        private readonly HashSet<Guid> _games = new();
        public DateOnly Date { get; } = date;
        public int AchievementCount { get; private set; }
        public int CompletionCount { get; private set; }
        public List<DesktopCalendarEvent> Events { get; } = new();
        public void AddPlaytime(TimeSpan duration, Guid gameId) { _ticks = checked(_ticks + duration.Ticks); _games.Add(gameId); }
        public void AddAchievement(Guid gameId) { AchievementCount++; _games.Add(gameId); }
        public void AddCompletion(Guid gameId) { CompletionCount++; _games.Add(gameId); }
        public DesktopCalendarDay Build() => new(Date, TimeSpan.FromTicks(_ticks), AchievementCount, CompletionCount, _games.Count, Events.OrderBy(item => item.OccurredAtUtc).ThenBy(item => item.Kind).ToArray());
    }
}
