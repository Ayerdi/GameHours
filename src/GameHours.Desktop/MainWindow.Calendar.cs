using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private DesktopActivityCalendarService? _calendarService;
    private DateOnly _calendarMonth;
    private DateOnly? _selectedCalendarDate;
    private bool _calendarBusy;
    private string _calendarMonthText = string.Empty;
    private string _calendarMonthSummaryText = "Sin actividad medida este mes.";
    private string _selectedCalendarDateText = "Selecciona un día";
    private string _selectedCalendarSummaryText = "Sesiones y logros aparecerán aquí.";

    public ObservableCollection<CalendarDayViewModel> CalendarDays { get; } = new();
    public ObservableCollection<CalendarEventViewModel> SelectedCalendarEvents { get; } = new();

    public string CalendarMonthText
    {
        get => _calendarMonthText;
        private set => SetField(ref _calendarMonthText, value);
    }

    public string CalendarMonthSummaryText
    {
        get => _calendarMonthSummaryText;
        private set => SetField(ref _calendarMonthSummaryText, value);
    }

    public string SelectedCalendarDateText
    {
        get => _selectedCalendarDateText;
        private set => SetField(ref _selectedCalendarDateText, value);
    }

    public string SelectedCalendarSummaryText
    {
        get => _selectedCalendarSummaryText;
        private set => SetField(ref _selectedCalendarSummaryText, value);
    }

    private void InitializeCalendar()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _calendarMonth = new DateOnly(today.Year, today.Month, 1);
        _calendarService = new DesktopActivityCalendarService(DatabasePathText);
        CalendarMonthText = FormatMonth(_calendarMonth);
    }

    private async void CalendarNav_Click(object sender, RoutedEventArgs e)
    {
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Calendar);
        await LoadCalendarMonthAsync(
            _calendarMonth,
            preferredDate: IsCurrentMonth(_calendarMonth)
                ? DateOnly.FromDateTime(DateTime.Now)
                : null);
    }

    private async void CalendarPrevious_Click(object sender, RoutedEventArgs e)
    {
        await LoadCalendarMonthAsync(_calendarMonth.AddMonths(-1));
    }

    private async void CalendarNext_Click(object sender, RoutedEventArgs e)
    {
        await LoadCalendarMonthAsync(_calendarMonth.AddMonths(1));
    }

    private async void CalendarToday_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        await LoadCalendarMonthAsync(
            new DateOnly(today.Year, today.Month, 1),
            today);
    }

    private async void CalendarRefresh_Click(object sender, RoutedEventArgs e)
    {
        await LoadCalendarMonthAsync(_calendarMonth, _selectedCalendarDate);
    }

    private void CalendarDay_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: CalendarDayViewModel day } ||
            day.Day is null)
        {
            return;
        }

        SelectCalendarDay(day.Day);
    }

    private async Task LoadCalendarMonthAsync(
        DateOnly month,
        DateOnly? preferredDate = null)
    {
        if (_calendarBusy || _calendarService is null)
        {
            return;
        }

        _calendarBusy = true;
        SetCalendarButtonsEnabled(false);
        CalendarMonthSummaryText = "Cargando actividad…";

        try
        {
            var normalizedMonth = new DateOnly(month.Year, month.Month, 1);
            var result = await _calendarService.LoadMonthAsync(normalizedMonth);
            _calendarMonth = normalizedMonth;
            CalendarMonthText = FormatMonth(normalizedMonth);
            CalendarMonthSummaryText = FormatMonthSummary(result);
            BuildCalendarGrid(result);

            var selectedDate = preferredDate is DateOnly requested &&
                               requested.Year == normalizedMonth.Year &&
                               requested.Month == normalizedMonth.Month
                ? requested
                : result.Days.LastOrDefault(day => day.Events.Count > 0)?.Date
                  ?? result.Days.FirstOrDefault()?.Date;
            SelectCalendarDay(selectedDate);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or OverflowException)
        {
            CalendarDays.Clear();
            SelectedCalendarEvents.Clear();
            CalendarMonthSummaryText = "No se pudo cargar el calendario local.";
            SelectedCalendarDateText = "Calendario no disponible";
            SelectedCalendarSummaryText = exception.Message;
        }
        finally
        {
            _calendarBusy = false;
            SetCalendarButtonsEnabled(true);
        }
    }

    private async Task RefreshCalendarIfVisibleAsync()
    {
        if (CalendarView.Visibility != Visibility.Visible || _calendarBusy)
        {
            return;
        }

        await LoadCalendarMonthAsync(_calendarMonth, _selectedCalendarDate);
    }

    private void BuildCalendarGrid(DesktopCalendarMonth month)
    {
        CalendarDays.Clear();

        var first = month.Days.FirstOrDefault()?.Date ?? month.Month;
        var mondayBasedOffset = ((int)first.DayOfWeek + 6) % 7;
        for (var index = 0; index < mondayBasedOffset; index++)
        {
            CalendarDays.Add(CalendarDayViewModel.Placeholder());
        }

        foreach (var day in month.Days)
        {
            CalendarDays.Add(new CalendarDayViewModel(day, month.BusiestDayPlaytime));
        }

        while (CalendarDays.Count < 42)
        {
            CalendarDays.Add(CalendarDayViewModel.Placeholder());
        }
    }

    private void SelectCalendarDay(DateOnly? date)
    {
        _selectedCalendarDate = date;
        foreach (var cell in CalendarDays)
        {
            cell.IsSelected = date is not null && cell.Day?.Date == date.Value;
        }

        SelectedCalendarEvents.Clear();
        if (date is null)
        {
            SelectedCalendarDateText = "Selecciona un día";
            SelectedCalendarSummaryText = "Sesiones y logros aparecerán aquí.";
            return;
        }

        var selected = CalendarDays
            .Select(cell => cell.Day)
            .FirstOrDefault(day => day?.Date == date.Value);
        if (selected is null)
        {
            SelectedCalendarDateText = FormatDayHeading(date.Value);
            SelectedCalendarSummaryText = "Sin actividad medida ni logros registrados.";
            return;
        }

        SelectedCalendarDateText = FormatDayHeading(selected.Date);
        SelectedCalendarSummaryText = FormatDaySummary(selected);
        foreach (var item in selected.Events)
        {
            SelectedCalendarEvents.Add(new CalendarEventViewModel(item));
        }
    }

    private void SetCalendarButtonsEnabled(bool enabled)
    {
        if (CalendarPreviousButton is null ||
            CalendarNextButton is null ||
            CalendarTodayButton is null ||
            CalendarRefreshButton is null)
        {
            return;
        }

        CalendarPreviousButton.IsEnabled = enabled;
        CalendarNextButton.IsEnabled = enabled;
        CalendarTodayButton.IsEnabled = enabled;
        CalendarRefreshButton.IsEnabled = enabled;
    }

    private static string FormatMonthSummary(DesktopCalendarMonth month)
    {
        if (month.MeasuredPlaytime <= TimeSpan.Zero && month.AchievementCount == 0)
        {
            return "Sin sesiones medidas ni logros registrados este mes.";
        }

        var games = month.GameCount == 1 ? "1 juego" : $"{month.GameCount} juegos";
        var achievements = month.AchievementCount == 1
            ? "1 logro"
            : $"{month.AchievementCount} logros";
        return $"{FormatDiaryDuration(month.MeasuredPlaytime)} jugadas · {games} · {achievements}";
    }

    private static string FormatDaySummary(DesktopCalendarDay day)
    {
        if (day.MeasuredPlaytime <= TimeSpan.Zero && day.AchievementCount == 0)
        {
            return "Sin sesiones medidas ni logros registrados.";
        }

        var games = day.GameCount == 1 ? "1 juego" : $"{day.GameCount} juegos";
        var achievements = day.AchievementCount == 1 ? "1 logro" : $"{day.AchievementCount} logros";
        return $"{FormatDiaryDuration(day.MeasuredPlaytime)} jugadas · {games} · {achievements}";
    }

    private static string FormatMonth(DateOnly month)
    {
        var text = month.ToDateTime(TimeOnly.MinValue)
            .ToString("MMMM yyyy", CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(text)
            ? text
            : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static string FormatDayHeading(DateOnly date)
    {
        var text = date.ToDateTime(TimeOnly.MinValue)
            .ToString("dddd, d 'de' MMMM", CultureInfo.CurrentCulture);
        return string.IsNullOrEmpty(text)
            ? text
            : char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
    }

    private static string FormatDiaryDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
        {
            return "0 min";
        }

        var totalMinutes = Math.Max(1, (int)Math.Round(value.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours switch
        {
            0 => $"{minutes} min",
            _ when minutes == 0 => $"{hours} h",
            _ => $"{hours} h {minutes} min"
        };
    }

    private static bool IsCurrentMonth(DateOnly month)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        return month.Year == today.Year && month.Month == today.Month;
    }

    public sealed class CalendarDayViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;

        internal DesktopCalendarDay? Day { get; }
        public string DayNumberText => Day?.Date.Day.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        public string PlaytimeText => Day is null || Day.MeasuredPlaytime <= TimeSpan.Zero
            ? string.Empty
            : FormatCompactDuration(Day.MeasuredPlaytime);
        public string AchievementText => Day is null || Day.AchievementCount == 0
            ? string.Empty
            : $"🏆 {Day.AchievementCount}";
        public double ActivityOpacity { get; }
        public Thickness SelectionThickness => _isSelected ? new Thickness(2) : new Thickness(0);
        public string ToolTipText => Day is null
            ? string.Empty
            : $"{Day.Date:dd/MM/yyyy} · {FormatDiaryDuration(Day.MeasuredPlaytime)} · {Day.AchievementCount} logros";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectionThickness)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal CalendarDayViewModel(DesktopCalendarDay day, TimeSpan busiestDay)
        {
            Day = day;
            if (day.MeasuredPlaytime > TimeSpan.Zero && busiestDay > TimeSpan.Zero)
            {
                var ratio = Math.Clamp(
                    day.MeasuredPlaytime.TotalSeconds / busiestDay.TotalSeconds,
                    0d,
                    1d);
                ActivityOpacity = 0.12d + 0.55d * Math.Sqrt(ratio);
            }
        }

        private CalendarDayViewModel()
        {
        }

        internal static CalendarDayViewModel Placeholder() => new();

        private static string FormatCompactDuration(TimeSpan value)
        {
            var totalMinutes = Math.Max(1, (int)Math.Round(value.TotalMinutes));
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            if (hours == 0)
            {
                return $"{minutes}m";
            }

            if (minutes == 0 || hours >= 10)
            {
                return $"{hours}h";
            }

            return $"{hours}h {minutes}m";
        }
    }

    public sealed class CalendarEventViewModel
    {
        public string WhenText { get; }
        public string GameTitle { get; }
        public string KindText { get; }
        public string TitleText { get; }
        public string DetailText { get; }

        internal CalendarEventViewModel(DesktopCalendarEvent item)
        {
            var local = item.OccurredAtUtc.ToLocalTime();
            GameTitle = item.GameTitle;

            if (item.Kind == DesktopCalendarEventKind.AchievementUnlocked)
            {
                WhenText = item.IsObservedTimeFallback
                    ? $"Detectado · {local:HH:mm}"
                    : local.ToString("HH:mm");
                KindText = "🏆 Logro";
                TitleText = string.IsNullOrWhiteSpace(item.Title) ? "Logro desbloqueado" : item.Title;
                DetailText = item.Description ?? string.Empty;
                return;
            }

            WhenText = local.ToString("HH:mm");
            KindText = item.Duration is TimeSpan duration
                ? $"Sesión · {FormatDiaryDuration(duration)}"
                : "Sesión";
            TitleText = item.StartedBeforeLocalDay
                ? "Continuación de la sesión anterior"
                : "Sesión medida";
            DetailText = item.ContinuesAfterLocalDay
                ? "Continúa al día siguiente."
                : SessionEndText(item.EndReason);
        }

        private static string SessionEndText(string? endReason) => endReason switch
        {
            "GracefulShutdown" => "GameHours se cerró limpiamente.",
            "RecoveredFromCheckpoint" => "Sesión recuperada desde el último checkpoint.",
            "ReconciledStop" or "Stopped" => "Juego cerrado.",
            null or "" => string.Empty,
            _ => endReason ?? string.Empty
        };
    }
}
