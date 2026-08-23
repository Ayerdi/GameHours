using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GameHours.Desktop;

public partial class ActivityCalendarView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly DesktopActivityCalendarService _service;
    private readonly string _databasePath;
    private DateOnly _month;
    private DateOnly? _selectedDate;
    private bool _busy;
    private bool _loadedOnce;
    private string _monthText = string.Empty;
    private string _monthSummaryText = "Sin actividad medida este mes.";
    private string _selectedDateText = "Selecciona un día";
    private string _selectedDaySummaryText = "Sesiones y logros aparecerán aquí.";

    public ObservableCollection<CalendarDayViewModel> Days { get; } = new();
    public ObservableCollection<CalendarEventViewModel> SelectedDayEvents { get; } = new();

    public string MonthText { get => _monthText; private set => SetField(ref _monthText, value); }
    public string MonthSummaryText { get => _monthSummaryText; private set => SetField(ref _monthSummaryText, value); }
    public string SelectedDateText { get => _selectedDateText; private set => SetField(ref _selectedDateText, value); }
    public string SelectedDaySummaryText { get => _selectedDaySummaryText; private set => SetField(ref _selectedDaySummaryText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ActivityCalendarView(string databasePath)
    {
        _databasePath = databasePath;
        _service = new DesktopActivityCalendarService(databasePath);
        var today = DateOnly.FromDateTime(DateTime.Now);
        _month = new DateOnly(today.Year, today.Month, 1);
        _monthText = FormatMonth(_month);

        InitializeComponent();
        DataContext = this;
        Loaded += View_Loaded;
        AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler(SessionEvent_PreviewMouseLeftButtonUp));
    }

    public Task RefreshAsync() => LoadMonthAsync(_month, _selectedDate);

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce) return;
        _loadedOnce = true;
        var today = DateOnly.FromDateTime(DateTime.Now);
        await LoadMonthAsync(_month, today);
    }

    private void SessionEvent_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SessionDetailNavigation.TryOpenFromVisual(e.OriginalSource as DependencyObject, _databasePath, Window.GetWindow(this)))
        {
            e.Handled = true;
        }
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await LoadMonthAsync(_month.AddMonths(-1));
    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadMonthAsync(_month.AddMonths(1));

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        await LoadMonthAsync(new DateOnly(today.Year, today.Month, 1), today);
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Day_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CalendarDayViewModel { Day: not null } cell }) SelectDay(cell.Day.Date);
    }

    private async Task LoadMonthAsync(DateOnly month, DateOnly? preferredDate = null)
    {
        if (_busy) return;
        _busy = true;
        SetButtonsEnabled(false);
        MonthSummaryText = "Cargando actividad…";

        try
        {
            var normalized = new DateOnly(month.Year, month.Month, 1);
            var result = await _service.LoadMonthAsync(normalized);
            _month = normalized;
            MonthText = FormatMonth(normalized);
            MonthSummaryText = FormatMonthSummary(result);
            BuildGrid(result);

            var selected = preferredDate is DateOnly requested && requested.Year == normalized.Year && requested.Month == normalized.Month
                ? requested
                : result.Days.LastOrDefault(day => day.Events.Count > 0)?.Date ?? result.Days.FirstOrDefault()?.Date;
            SelectDay(selected);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or OverflowException)
        {
            Days.Clear();
            SelectedDayEvents.Clear();
            MonthSummaryText = "No se pudo cargar el calendario local.";
            SelectedDateText = "Calendario no disponible";
            SelectedDaySummaryText = exception.Message;
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void BuildGrid(DesktopCalendarMonth month)
    {
        Days.Clear();
        var first = month.Days.FirstOrDefault()?.Date ?? month.Month;
        var mondayBasedOffset = ((int)first.DayOfWeek + 6) % 7;
        for (var index = 0; index < mondayBasedOffset; index++) Days.Add(CalendarDayViewModel.Placeholder());
        foreach (var day in month.Days) Days.Add(new CalendarDayViewModel(day, month.BusiestDayPlaytime));
        while (Days.Count < 42) Days.Add(CalendarDayViewModel.Placeholder());
    }

    private void SelectDay(DateOnly? date)
    {
        _selectedDate = date;
        foreach (var cell in Days) cell.IsSelected = date is not null && cell.Day?.Date == date.Value;

        SelectedDayEvents.Clear();
        if (date is null)
        {
            SelectedDateText = "Selecciona un día";
            SelectedDaySummaryText = "Sesiones, logros e hitos aparecerán aquí.";
            return;
        }

        var selected = Days.Select(cell => cell.Day).FirstOrDefault(day => day?.Date == date.Value);
        SelectedDateText = FormatDayHeading(date.Value);
        if (selected is null)
        {
            SelectedDaySummaryText = "Sin actividad medida, logros o hitos registrados.";
            return;
        }

        SelectedDaySummaryText = FormatDaySummary(selected);
        foreach (var item in selected.Events) SelectedDayEvents.Add(new CalendarEventViewModel(item));
    }

    private void SetButtonsEnabled(bool enabled)
    {
        PreviousButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
        TodayButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    private static string FormatMonthSummary(DesktopCalendarMonth month)
    {
        if (month.MeasuredPlaytime <= TimeSpan.Zero && month.AchievementCount == 0 && month.CompletionCount == 0) return "Sin sesiones medidas, logros o hitos registrados este mes.";
        var games = month.GameCount == 1 ? "1 juego" : $"{month.GameCount} juegos";
        var achievements = month.AchievementCount == 1 ? "1 logro" : $"{month.AchievementCount} logros";
        var summary = $"{FormatDiaryDuration(month.MeasuredPlaytime)} jugadas · {games} · {achievements}";
        return month.CompletionCount switch { 0 => summary, 1 => $"{summary} · ★ 1 completado al 100 %", _ => $"{summary} · ★ {month.CompletionCount} completados al 100 %" };
    }

    private static string FormatDaySummary(DesktopCalendarDay day)
    {
        if (day.MeasuredPlaytime <= TimeSpan.Zero && day.AchievementCount == 0 && day.CompletionCount == 0) return "Sin sesiones medidas, logros o hitos registrados.";
        var games = day.GameCount == 1 ? "1 juego" : $"{day.GameCount} juegos";
        var achievements = day.AchievementCount == 1 ? "1 logro" : $"{day.AchievementCount} logros";
        var summary = $"{FormatDiaryDuration(day.MeasuredPlaytime)} jugadas · {games} · {achievements}";
        return day.CompletionCount switch { 0 => summary, 1 => $"{summary} · ★ 100 % completado", _ => $"{summary} · ★ {day.CompletionCount} juegos al 100 %" };
    }

    private static string FormatMonth(DateOnly month) => Capitalize(month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.CurrentCulture));
    private static string FormatDayHeading(DateOnly date) => Capitalize(date.ToDateTime(TimeOnly.MinValue).ToString("dddd, d 'de' MMMM", CultureInfo.CurrentCulture));
    private static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..];

    private static string FormatDiaryDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0 min";
        var totalMinutes = Math.Max(1, (int)Math.Round(value.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        return hours switch { 0 => $"{minutes} min", _ when minutes == 0 => $"{hours} h", _ => $"{hours} h {minutes} min" };
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public sealed class CalendarDayViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        internal DesktopCalendarDay? Day { get; }
        public string DayNumberText => Day?.Date.Day.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        public string PlaytimeText => Day is null || Day.MeasuredPlaytime <= TimeSpan.Zero ? string.Empty : FormatCompactDuration(Day.MeasuredPlaytime);
        public string AchievementText => Day is null || Day.AchievementCount == 0 ? string.Empty : $"🏆 {Day.AchievementCount}";
        public string CompletionText => Day is null || Day.CompletionCount == 0 ? string.Empty : Day.CompletionCount == 1 ? "★ 100 %" : $"★ {Day.CompletionCount}×100 %";
        public double ActivityOpacity { get; }
        public Thickness SelectionThickness => _isSelected ? new Thickness(2) : new Thickness(0);
        public string ToolTipText => Day is null ? string.Empty : $"{Day.Date:dd/MM/yyyy} · {FormatDiaryDuration(Day.MeasuredPlaytime)} · {Day.AchievementCount} logros · {Day.CompletionCount} hitos 100 %";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
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
                var ratio = Math.Clamp(day.MeasuredPlaytime.TotalSeconds / busiestDay.TotalSeconds, 0d, 1d);
                ActivityOpacity = 0.12d + 0.55d * Math.Sqrt(ratio);
            }
        }

        private CalendarDayViewModel() { }
        internal static CalendarDayViewModel Placeholder() => new();

        private static string FormatCompactDuration(TimeSpan value)
        {
            var totalMinutes = Math.Max(1, (int)Math.Round(value.TotalMinutes));
            var hours = totalMinutes / 60;
            var minutes = totalMinutes % 60;
            if (hours == 0) return $"{minutes}m";
            return minutes == 0 || hours >= 10 ? $"{hours}h" : $"{hours}h {minutes}m";
        }
    }

    public sealed class CalendarEventViewModel
    {
        public Guid? SessionId { get; }
        public string WhenText { get; }
        public string GameTitle { get; }
        public string KindText { get; }
        public string TitleText { get; }
        public string DetailText { get; }

        internal CalendarEventViewModel(DesktopCalendarEvent item)
        {
            SessionId = item.SessionId;
            var local = item.OccurredAtUtc.ToLocalTime();
            GameTitle = item.GameTitle;
            if (item.Kind == DesktopCalendarEventKind.AchievementCompleted)
            {
                WhenText = item.IsObservedTimeFallback ? $"Detectado · {local:HH:mm}" : local.ToString("HH:mm");
                KindText = "★ 100 %";
                TitleText = item.Title ?? "100 % completado";
                DetailText = item.IsObservedTimeFallback ? $"{item.Description} Hora aproximada: GameHours no conoce el timestamp exacto del último logro." : item.Description ?? string.Empty;
                return;
            }

            if (item.Kind == DesktopCalendarEventKind.AchievementUnlocked)
            {
                WhenText = item.IsObservedTimeFallback ? $"Detectado · {local:HH:mm}" : local.ToString("HH:mm");
                KindText = "🏆 Logro";
                TitleText = string.IsNullOrWhiteSpace(item.Title) ? "Logro desbloqueado" : item.Title;
                DetailText = item.Description ?? string.Empty;
                return;
            }

            WhenText = local.ToString("HH:mm");
            KindText = item.Duration is TimeSpan duration ? $"Sesión · {FormatDiaryDuration(duration)}" : "Sesión";
            TitleText = item.StartedBeforeLocalDay ? "Continuación de la sesión anterior" : "Sesión medida";
            var endText = item.ContinuesAfterLocalDay ? "Continúa al día siguiente." : SessionEndText(item.EndReason);
            DetailText = string.IsNullOrWhiteSpace(endText)
                ? "Pulsa para ver foco, activo y AFK de la sesión completa."
                : $"{endText} Pulsa para ver foco, activo y AFK de la sesión completa.";
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
