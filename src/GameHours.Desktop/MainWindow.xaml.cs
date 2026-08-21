using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameHours.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    private readonly DesktopHost _host;
    private readonly WindowsStartupService _startupService;
    private readonly DispatcherTimer _sessionTimer;
    private bool _allowClose;
    private bool _initializingStartup;
    private DateTimeOffset? _activeGameStartedAtUtc;
    private string _statusText = "Preparando…";
    private string _activeGameText = "Ningún juego activo";
    private string _activeGameSubtitle = "GameHours está esperando en segundo plano.";
    private string _activeGameElapsedText = "En espera";
    private string _gameCountText = "0";
    private string _totalPlaytimeText = "0 h";

    public ObservableCollection<GameRowViewModel> Games { get; } = new();
    public ObservableCollection<ActivityRowViewModel> RecentActivity { get; } = new();

    public string DatabasePathText { get; }

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string ActiveGameText
    {
        get => _activeGameText;
        private set => SetField(ref _activeGameText, value);
    }

    public string ActiveGameSubtitle
    {
        get => _activeGameSubtitle;
        private set => SetField(ref _activeGameSubtitle, value);
    }

    public string ActiveGameElapsedText
    {
        get => _activeGameElapsedText;
        private set => SetField(ref _activeGameElapsedText, value);
    }

    public string GameCountText
    {
        get => _gameCountText;
        private set => SetField(ref _gameCountText, value);
    }

    public string TotalPlaytimeText
    {
        get => _totalPlaytimeText;
        private set => SetField(ref _totalPlaytimeText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<Task>? ExitRequested;

    public MainWindow(DesktopHost host, WindowsStartupService startupService)
    {
        _host = host;
        _startupService = startupService;
        DatabasePathText = _host.DatabasePath;

        InitializeComponent();
        DataContext = this;

        SourceInitialized += Window_SourceInitialized;
        _host.StatusChanged += OnStatusChanged;

        _sessionTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _sessionTimer.Tick += (_, _) => UpdateActiveSessionClock();
        _sessionTimer.Start();

        _initializingStartup = true;
        try
        {
            StartupCheckBox.IsEnabled = _startupService.IsSupported;
            StartupCheckBox.IsChecked = _startupService.IsSupported && _startupService.IsEnabled;
        }
        finally
        {
            _initializingStartup = false;
        }

        ShowSection(DesktopSection.Library);
    }

    public void ApplyInitialStatus(DesktopStatus status) => ApplyStatus(status);

    public void AllowClose() => _allowClose = true;

    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        var result = DwmSetWindowAttribute(
            handle,
            DwmUseImmersiveDarkMode,
            ref enabled,
            Marshal.SizeOf<int>());
        if (result != 0)
        {
            DwmSetWindowAttribute(
                handle,
                DwmUseImmersiveDarkModeBefore20H1,
                ref enabled,
                Marshal.SizeOf<int>());
        }
    }

    private void OnStatusChanged(DesktopStatus status)
    {
        if (Dispatcher.CheckAccess())
        {
            ApplyStatus(status);
            return;
        }

        Dispatcher.Invoke(() => ApplyStatus(status));
    }

    private void ApplyStatus(DesktopStatus status)
    {
        StatusText = status.StatusText;
        _activeGameStartedAtUtc = status.ActiveGameStartedAtUtc;

        if (string.IsNullOrWhiteSpace(status.ActiveGameTitle))
        {
            ActiveGameText = "Ningún juego activo";
            ActiveGameSubtitle = status.IsTracking
                ? "Listo para detectar el próximo juego."
                : "El tracker está detenido.";
            ActiveGameElapsedText = status.IsTracking ? "En espera" : "Detenido";
        }
        else
        {
            ActiveGameText = status.ActiveGameTitle;
            ActiveGameSubtitle = "Sesión en curso · guardado local y automático";
            UpdateActiveSessionClock();
        }

        Games.Clear();
        foreach (var game in status.Games)
        {
            Games.Add(new GameRowViewModel(game));
        }

        RecentActivity.Clear();
        foreach (var activity in status.RecentActivity)
        {
            RecentActivity.Add(new ActivityRowViewModel(activity));
        }

        GameCountText = status.Games.Count.ToString();
        var totalTicks = status.Games.Aggregate(
            0L,
            (total, game) => checked(total + game.TotalPlaytime.Ticks));
        TotalPlaytimeText = FormatDuration(TimeSpan.FromTicks(totalTicks));
    }

    private void UpdateActiveSessionClock()
    {
        if (_activeGameStartedAtUtc is null)
        {
            return;
        }

        var elapsed = DateTimeOffset.UtcNow - _activeGameStartedAtUtc.Value;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        ActiveGameElapsedText = FormatClock(elapsed);
    }

    private void LibraryNav_Click(object sender, RoutedEventArgs e) =>
        ShowSection(DesktopSection.Library);

    private void ActivityNav_Click(object sender, RoutedEventArgs e) =>
        ShowSection(DesktopSection.Activity);

    private void SettingsNav_Click(object sender, RoutedEventArgs e) =>
        ShowSection(DesktopSection.Settings);

    private void ShowSection(DesktopSection section)
    {
        LibraryView.Visibility = section == DesktopSection.Library
            ? Visibility.Visible
            : Visibility.Collapsed;
        ActivityView.Visibility = section == DesktopSection.Activity
            ? Visibility.Visible
            : Visibility.Collapsed;
        SettingsView.Visibility = section == DesktopSection.Settings
            ? Visibility.Visible
            : Visibility.Collapsed;

        var selected = (Brush)FindResource("SurfaceAltBrush");
        LibraryNavButton.Background = section == DesktopSection.Library ? selected : Brushes.Transparent;
        ActivityNavButton.Background = section == DesktopSection.Activity ? selected : Brushes.Transparent;
        SettingsNavButton.Background = section == DesktopSection.Settings ? selected : Brushes.Transparent;
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _host.RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "No se pudo actualizar la información local",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private async void Exit_Click(object sender, RoutedEventArgs e)
    {
        if (ExitRequested is null)
        {
            return;
        }

        IsEnabled = false;
        StatusText = "Guardando antes de salir…";
        try
        {
            await ExitRequested.Invoke();
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void StartupCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializingStartup || !_startupService.IsSupported)
        {
            return;
        }

        try
        {
            _startupService.SetEnabled(StartupCheckBox.IsChecked == true);
        }
        catch (Exception exception)
        {
            _initializingStartup = true;
            try
            {
                StartupCheckBox.IsChecked = _startupService.IsEnabled;
            }
            finally
            {
                _initializingStartup = false;
            }

            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "No se pudo cambiar el inicio con Windows",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            _sessionTimer.Stop();
            _host.StatusChanged -= OnStatusChanged;
            return;
        }

        e.Cancel = true;
        Hide();
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalHours >= 100)
        {
            return $"{duration.TotalHours:0} h";
        }

        if (duration.TotalHours >= 10)
        {
            return $"{duration.TotalHours:0.0} h";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{duration.TotalHours:0.00} h";
        }

        return $"{Math.Max(0, duration.TotalMinutes):0} min";
    }

    private static string FormatClock(TimeSpan duration)
    {
        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}:{duration.Minutes:00}:{duration.Seconds:00}";
        }

        return $"{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private static string FormatActivityDate(DateTimeOffset? occurredAtUtc)
    {
        if (occurredAtUtc is null)
        {
            return "—";
        }

        var local = occurredAtUtc.Value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        var date = local.Date;
        if (date == today)
        {
            return $"Hoy · {local:HH:mm}";
        }

        if (date == today.AddDays(-1))
        {
            return $"Ayer · {local:HH:mm}";
        }

        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("dd MMM · HH:mm")
            : local.ToString("dd/MM/yy · HH:mm");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public sealed class GameRowViewModel
    {
        public string Title { get; }
        public string LastActivityText { get; }
        public string TotalText { get; }
        public string MeasuredText { get; }
        public string EstimatedText { get; }

        public GameRowViewModel(DesktopGameRow game)
        {
            Title = game.Title;
            LastActivityText = FormatActivityDate(game.LastActivityAtUtc);
            TotalText = FormatDuration(game.TotalPlaytime);
            MeasuredText = FormatDuration(game.MeasuredPlaytime);
            EstimatedText = game.EstimatedPlaytime > TimeSpan.Zero
                ? FormatDuration(game.EstimatedPlaytime)
                : "—";
        }
    }

    public sealed class ActivityRowViewModel
    {
        public string GameTitle { get; }
        public string WhenText { get; }
        public string DurationText { get; }
        public string ReasonText { get; }

        public ActivityRowViewModel(DesktopActivityRow activity)
        {
            GameTitle = activity.GameTitle;
            WhenText = FormatActivityDate(activity.EndedAtUtc);
            DurationText = FormatDuration(activity.Duration);
            ReasonText = activity.EndReason switch
            {
                "GracefulShutdown" => "Salida de GameHours",
                "RecoveredFromCheckpoint" => "Sesión recuperada",
                "ReconciledStop" => "Juego cerrado",
                "Stopped" => "Juego cerrado",
                null or "" => "Sesión medida",
                _ => activity.EndReason ?? "Sesión medida"
            };
        }
    }

    private enum DesktopSection
    {
        Library,
        Activity,
        Settings
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
