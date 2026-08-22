using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class StatisticsView : UserControl, INotifyPropertyChanged
{
    private readonly DesktopStatisticsService _service;
    private DateOnly _month;
    private bool _busy;
    private bool _loadedOnce;

    private string _monthText = string.Empty;
    private string _monthPlaytimeText = "0 min";
    private string _monthActiveDaysText = "0";
    private string _monthGameCountText = "0";
    private string _monthAchievementCountText = "0";
    private string _monthAverageText = "0 min";
    private string _monthMostPlayedTitleText = "Sin actividad medida";
    private string _monthMostPlayedValueText = "—";
    private string _monthBusiestDayTitleText = "Sin actividad medida";
    private string _monthBusiestDayValueText = "—";
    private string _lifetimeKnownText = "0 min";
    private string _lifetimeBreakdownText = "0 min medidos · 0 min históricos";
    private string _lifetimeGameCountText = "0";
    private string _lifetimeSessionsAchievementsText = "0 / 0";
    private string _lifetimeCompletedText = "0";
    private string _lifetimeMostPlayedTitleText = "Sin actividad conocida";
    private string _lifetimeMostPlayedValueText = "—";
    private string _longestSessionTitleText = "Sin sesiones medidas";
    private string _longestSessionValueText = "—";
    private string _currentStreakText = "Sin racha activa";
    private string _longestStreakText = "Sin rachas registradas";
    private string _firstKnownActivityText = "—";
    private string _statusText = "Preparando estadísticas…";

    public string MonthText { get => _monthText; private set => SetField(ref _monthText, value); }
    public string MonthPlaytimeText { get => _monthPlaytimeText; private set => SetField(ref _monthPlaytimeText, value); }
    public string MonthActiveDaysText { get => _monthActiveDaysText; private set => SetField(ref _monthActiveDaysText, value); }
    public string MonthGameCountText { get => _monthGameCountText; private set => SetField(ref _monthGameCountText, value); }
    public string MonthAchievementCountText { get => _monthAchievementCountText; private set => SetField(ref _monthAchievementCountText, value); }
    public string MonthAverageText { get => _monthAverageText; private set => SetField(ref _monthAverageText, value); }
    public string MonthMostPlayedTitleText { get => _monthMostPlayedTitleText; private set => SetField(ref _monthMostPlayedTitleText, value); }
    public string MonthMostPlayedValueText { get => _monthMostPlayedValueText; private set => SetField(ref _monthMostPlayedValueText, value); }
    public string MonthBusiestDayTitleText { get => _monthBusiestDayTitleText; private set => SetField(ref _monthBusiestDayTitleText, value); }
    public string MonthBusiestDayValueText { get => _monthBusiestDayValueText; private set => SetField(ref _monthBusiestDayValueText, value); }
    public string LifetimeKnownText { get => _lifetimeKnownText; private set => SetField(ref _lifetimeKnownText, value); }
    public string LifetimeBreakdownText { get => _lifetimeBreakdownText; private set => SetField(ref _lifetimeBreakdownText, value); }
    public string LifetimeGameCountText { get => _lifetimeGameCountText; private set => SetField(ref _lifetimeGameCountText, value); }
    public string LifetimeSessionsAchievementsText { get => _lifetimeSessionsAchievementsText; private set => SetField(ref _lifetimeSessionsAchievementsText, value); }
    public string LifetimeCompletedText { get => _lifetimeCompletedText; private set => SetField(ref _lifetimeCompletedText, value); }
    public string LifetimeMostPlayedTitleText { get => _lifetimeMostPlayedTitleText; private set => SetField(ref _lifetimeMostPlayedTitleText, value); }
    public string LifetimeMostPlayedValueText { get => _lifetimeMostPlayedValueText; private set => SetField(ref _lifetimeMostPlayedValueText, value); }
    public string LongestSessionTitleText { get => _longestSessionTitleText; private set => SetField(ref _longestSessionTitleText, value); }
    public string LongestSessionValueText { get => _longestSessionValueText; private set => SetField(ref _longestSessionValueText, value); }
    public string CurrentStreakText { get => _currentStreakText; private set => SetField(ref _currentStreakText, value); }
    public string LongestStreakText { get => _longestStreakText; private set => SetField(ref _longestStreakText, value); }
    public string FirstKnownActivityText { get => _firstKnownActivityText; private set => SetField(ref _firstKnownActivityText, value); }
    public string StatusText { get => _statusText; private set => SetField(ref _statusText, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public StatisticsView(string databasePath)
    {
        _service = new DesktopStatisticsService(databasePath);
        var today = DateOnly.FromDateTime(DateTime.Now);
        _month = new DateOnly(today.Year, today.Month, 1);
        _monthText = FormatMonth(_month);

        InitializeComponent();
        DataContext = this;
        Loaded += View_Loaded;
    }

    public Task RefreshAsync() => LoadAsync(_month);

    private async void View_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loadedOnce)
        {
            return;
        }

        _loadedOnce = true;
        await LoadAsync(_month);
    }

    private async void Previous_Click(object sender, RoutedEventArgs e) => await LoadAsync(_month.AddMonths(-1));
    private async void Next_Click(object sender, RoutedEventArgs e) => await LoadAsync(_month.AddMonths(1));

    private async void Today_Click(object sender, RoutedEventArgs e)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        await LoadAsync(new DateOnly(today.Year, today.Month, 1));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task LoadAsync(DateOnly month)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetButtonsEnabled(false);
        StatusText = "Calculando estadísticas locales…";

        try
        {
            var normalized = new DateOnly(month.Year, month.Month, 1);
            var snapshot = await _service.LoadAsync(normalized);
            _month = normalized;
            Apply(snapshot);
            StatusText = "Estadísticas calculadas a partir de datos locales de GameHours.";
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or OverflowException)
        {
            StatusText = $"No se pudieron calcular las estadísticas: {exception.Message}";
        }
        finally
        {
            _busy = false;
            SetButtonsEnabled(true);
        }
    }

    private void Apply(DesktopStatisticsSnapshot snapshot)
    {
        var month = snapshot.Month;
        var lifetime = snapshot.Lifetime;

        MonthText = FormatMonth(month.Month);
        MonthPlaytimeText = FormatDuration(month.MeasuredPlaytime);
        MonthActiveDaysText = month.ActiveDays.ToString(CultureInfo.InvariantCulture);
        MonthGameCountText = month.GameCount.ToString(CultureInfo.InvariantCulture);
        MonthAchievementCountText = month.AchievementCount.ToString(CultureInfo.InvariantCulture);
        MonthAverageText = FormatDuration(month.AveragePerActiveDay);
        MonthMostPlayedTitleText = month.MostPlayedGameTitle ?? "Sin actividad medida";
        MonthMostPlayedValueText = month.MostPlayedGameTitle is null ? "—" : FormatDuration(month.MostPlayedGameDuration);
        MonthBusiestDayTitleText = month.BusiestDay is DateOnly busiestDay ? FormatDay(busiestDay) : "Sin actividad medida";
        MonthBusiestDayValueText = month.BusiestDay is null ? "—" : FormatDuration(month.BusiestDayDuration);

        LifetimeKnownText = FormatDuration(lifetime.KnownPlaytime);
        LifetimeBreakdownText = $"{FormatDuration(lifetime.MeasuredPlaytime)} medidos · {FormatDuration(lifetime.HistoricalPlaytime)} históricos";
        LifetimeGameCountText = lifetime.GameCount.ToString(CultureInfo.InvariantCulture);
        LifetimeSessionsAchievementsText = $"{lifetime.SessionCount.ToString(CultureInfo.InvariantCulture)} / {lifetime.UnlockedAchievementCount.ToString(CultureInfo.InvariantCulture)}";
        LifetimeCompletedText = lifetime.CompletedGameCount.ToString(CultureInfo.InvariantCulture);
        LifetimeMostPlayedTitleText = lifetime.MostPlayedGameTitle ?? "Sin actividad conocida";
        LifetimeMostPlayedValueText = lifetime.MostPlayedGameTitle is null ? "—" : $"{FormatDuration(lifetime.MostPlayedGameDuration)} conocidas";
        LongestSessionTitleText = lifetime.LongestSessionGameTitle ?? "Sin sesiones medidas";
        LongestSessionValueText = lifetime.LongestSessionGameTitle is null ? "—" : FormatDuration(lifetime.LongestSessionDuration);
        CurrentStreakText = lifetime.Streaks.CurrentDays switch
        {
            0 => "Sin racha activa",
            1 => "Racha actual · 1 día",
            _ => $"Racha actual · {lifetime.Streaks.CurrentDays} días"
        };
        LongestStreakText = lifetime.Streaks.LongestDays switch
        {
            0 => "Sin rachas registradas",
            1 => "Máxima · 1 día",
            _ => $"Máxima · {lifetime.Streaks.LongestDays} días"
        };
        FirstKnownActivityText = lifetime.FirstKnownActivityAtUtc is DateTimeOffset first ? FormatKnownActivity(first) : "—";
    }

    private void SetButtonsEnabled(bool enabled)
    {
        PreviousButton.IsEnabled = enabled;
        NextButton.IsEnabled = enabled;
        TodayButton.IsEnabled = enabled;
        RefreshButton.IsEnabled = enabled;
    }

    private static string FormatMonth(DateOnly month) => Capitalize(month.ToDateTime(TimeOnly.MinValue).ToString("MMMM yyyy", CultureInfo.CurrentCulture));
    private static string FormatDay(DateOnly date) => Capitalize(date.ToDateTime(TimeOnly.MinValue).ToString("d 'de' MMMM", CultureInfo.CurrentCulture));
    private static string FormatKnownActivity(DateTimeOffset utc) => utc.ToLocalTime().ToString("d 'de' MMMM 'de' yyyy", CultureInfo.CurrentCulture);

    private static string FormatDuration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero) return "0 min";
        var totalMinutes = Math.Max(1, (long)Math.Round(value.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0) return $"{minutes} min";
        if (minutes == 0) return $"{hours} h";
        return $"{hours} h {minutes} min";
    }

    private static string Capitalize(string value) => string.IsNullOrEmpty(value) ? value : char.ToUpper(value[0], CultureInfo.CurrentCulture) + value[1..];

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
