using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameHours.Core.Updates;

namespace GameHours.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeBefore20H1 = 19;

    private readonly DesktopHost _host;
    private readonly WindowsStartupService _startupService;
    private readonly DesktopUpdateCoordinator _updates;
    private readonly DispatcherTimer _sessionTimer;
    private readonly DispatcherTimer _updateTimer;
    private bool _allowClose;
    private bool _initializingStartup;
    private bool _updateBusy;
    private bool _showWhatsNewOnNextForeground;
    private string? _lastNotifiedUpdateVersion;
    private AppUpdate? _availableUpdate;
    private Guid? _selectedGameId;
    private DateTimeOffset? _activeGameStartedAtUtc;
    private string _statusText = "Preparando…";
    private string _activeGameText = "Ningún juego activo";
    private string _activeGameSubtitle = "GameHours está esperando en segundo plano.";
    private string _activeGameElapsedText = "En espera";
    private string _gameCountText = "0";
    private string _totalPlaytimeText = "0 h";
    private string _updateStatusText;
    private string _updateProgressText = string.Empty;
    private GameDetailViewModel? _selectedGameDetail;

    public ObservableCollection<GameRowViewModel> Games { get; } = new();
    public ObservableCollection<ActivityRowViewModel> RecentActivity { get; } = new();

    public string DatabasePathText { get; }

    public string InstalledVersionText => _updates.CurrentVersion;

    public string UpdateChannelText => FormatUpdateChannel(_updates.Channel);

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

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetField(ref _updateStatusText, value);
    }

    public string UpdateProgressText
    {
        get => _updateProgressText;
        private set => SetField(ref _updateProgressText, value);
    }

    public GameDetailViewModel? SelectedGameDetail
    {
        get => _selectedGameDetail;
        private set => SetField(ref _selectedGameDetail, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Func<Task>? ExitRequested;
    public event Func<Task>? UpdateRestartRequested;
    public event Action<AppUpdate>? UpdateAvailable;

    public MainWindow(
        DesktopHost host,
        WindowsStartupService startupService,
        DesktopUpdateCoordinator updates)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _startupService = startupService ?? throw new ArgumentNullException(nameof(startupService));
        _updates = updates ?? throw new ArgumentNullException(nameof(updates));
        _updateStatusText = _updates.AvailabilityText;
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

        _updateTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromHours(6)
        };
        _updateTimer.Tick += UpdateTimer_Tick;

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

        ConfigureUpdateControls();
        ShowSection(DesktopSection.Library);
    }

    public void ApplyInitialStatus(DesktopStatus status) => ApplyStatus(status);

    public void AllowClose() => _allowClose = true;

    public async Task InitializeUpdatesAsync(bool showWhatsNew)
    {
        if (_updates.HasUnseenWhatsNew)
        {
            if (showWhatsNew && IsVisible)
            {
                ShowInstalledWhatsNew();
            }
            else
            {
                _showWhatsNewOnNextForeground = true;
            }
        }

        await CheckForUpdatesAsync(silent: true);
        if (_updates.CanSelfUpdate && !_updateTimer.IsEnabled)
        {
            _updateTimer.Start();
        }
    }

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

        if (_showWhatsNewOnNextForeground && _updates.HasUnseenWhatsNew)
        {
            _showWhatsNewOnNextForeground = false;
            Dispatcher.BeginInvoke(new Action(ShowInstalledWhatsNew));
        }
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

        if (_selectedGameId is Guid selectedGameId)
        {
            var selected = Games.FirstOrDefault(game => game.GameId == selectedGameId);
            if (selected is null)
            {
                _selectedGameId = null;
                SelectedGameDetail = null;
                ShowSection(DesktopSection.Library);
            }
            else
            {
                SelectedGameDetail = BuildGameDetail(selected);
            }
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

    private void LibraryNav_Click(object sender, RoutedEventArgs e)
    {
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Library);
    }

    private void ActivityNav_Click(object sender, RoutedEventArgs e)
    {
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Activity);
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Settings);
    }

    private void GameRow_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: GameRowViewModel game })
        {
            return;
        }

        _selectedGameId = game.GameId;
        SelectedGameDetail = BuildGameDetail(game);
        ShowSection(DesktopSection.GameDetail);
        e.Handled = true;
    }

    private void GameDetail_BackRequested(object? sender, EventArgs e)
    {
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Library);
    }

    private static GameDetailViewModel BuildGameDetail(GameRowViewModel game)
    {
        var recentSessions = game.RecentSessions.Take(12).ToArray();
        var activitySummary = game.MeasuredSessionCount == 0
            ? "Todavía no hay sesiones medidas por GameHours."
            : game.MeasuredSessionCount == 1
                ? "1 sesión medida por GameHours."
                : $"{game.MeasuredSessionCount} sesiones medidas por GameHours · mostrando las {Math.Min(12, game.MeasuredSessionCount)} más recientes.";

        return new GameDetailViewModel(
            game.GameId,
            game.Title,
            game.Initial,
            game.Icon,
            game.LastActivityText == "—"
                ? "Sin actividad registrada"
                : $"Última actividad · {game.LastActivityText}",
            game.TotalText,
            game.MeasuredText,
            game.EstimatedText,
            game.FirstActivityText,
            game.FirstMeasuredSessionText,
            game.MeasuredSessionCount.ToString(),
            activitySummary,
            string.IsNullOrWhiteSpace(game.ExecutablePath)
                ? "Sin ejecutable asociado"
                : game.ExecutablePath,
            recentSessions);
    }

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
        GameDetailPanel.Visibility = section == DesktopSection.GameDetail
            ? Visibility.Visible
            : Visibility.Collapsed;

        var selected = (System.Windows.Media.Brush)FindResource("SurfaceAltBrush");
        var librarySelected = section is DesktopSection.Library or DesktopSection.GameDetail;
        LibraryNavButton.Background = librarySelected
            ? selected
            : System.Windows.Media.Brushes.Transparent;
        ActivityNavButton.Background = section == DesktopSection.Activity
            ? selected
            : System.Windows.Media.Brushes.Transparent;
        SettingsNavButton.Background = section == DesktopSection.Settings
            ? selected
            : System.Windows.Media.Brushes.Transparent;
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

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(silent: false);

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is null || _updateBusy)
        {
            return;
        }

        var update = _availableUpdate;
        SetUpdateBusy(true);
        UpdateProgressBar.Visibility = Visibility.Visible;
        UpdateProgressBar.Value = 0;
        UpdateProgressText = "Descargando · 0%";
        UpdateStatusText = $"Descargando GameHours {update.Version}…";

        try
        {
            var progress = new Progress<int>(value =>
            {
                var bounded = Math.Clamp(value, 0, 100);
                UpdateProgressBar.Value = bounded;
                UpdateProgressText = $"Descargando · {bounded}%";
            });

            await _updates.DownloadAsync(update, progress);
            _updates.RememberReleaseNotes(update);
            UpdateProgressBar.Value = 100;
            UpdateProgressText = "Descarga completada";
            UpdateStatusText = "Actualización preparada · guardando GameHours antes de reiniciar…";

            _updates.PrepareApplyAndRestart(update);
            if (UpdateRestartRequested is null)
            {
                throw new InvalidOperationException(
                    "No hay un coordinador de cierre disponible para aplicar la actualización.");
            }

            await UpdateRestartRequested.Invoke();
        }
        catch (Exception exception)
        {
            UpdateStatusText = "No se pudo completar la actualización.";
            SetUpdateBusy(false);
            ConfigureUpdateControls();
            System.Windows.MessageBox.Show(
                this,
                exception.Message,
                "No se pudo actualizar GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void ShowReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_availableUpdate is { } update && !string.IsNullOrWhiteSpace(update.ReleaseNotesMarkdown))
        {
            ShowReleaseNotes(update.Version, update.ReleaseNotesMarkdown);
            return;
        }

        ShowInstalledWhatsNew();
    }

    private async Task CheckForUpdatesAsync(bool silent)
    {
        if (_updateBusy)
        {
            return;
        }

        if (!_updates.CanSelfUpdate)
        {
            UpdateStatusText = _updates.AvailabilityText;
            ConfigureUpdateControls();
            return;
        }

        SetUpdateBusy(true);
        UpdateStatusText = "Buscando actualizaciones…";

        try
        {
            var update = await _updates.CheckAsync();
            _availableUpdate = update;
            if (update is null)
            {
                UpdateStatusText = $"GameHours {_updates.CurrentVersion} está actualizado.";
            }
            else
            {
                var sizeMiB = update.FullPackageSizeBytes / (1024d * 1024d);
                var delivery = update.DeltaCount > 0
                    ? $" · {update.DeltaCount} delta disponible"
                    : string.Empty;
                UpdateStatusText =
                    $"Nueva versión {update.Version} disponible · paquete completo {sizeMiB:0.0} MiB{delivery}.";

                if (silent &&
                    !string.Equals(
                        _lastNotifiedUpdateVersion,
                        update.Version,
                        StringComparison.OrdinalIgnoreCase))
                {
                    _lastNotifiedUpdateVersion = update.Version;
                    UpdateAvailable?.Invoke(update);
                }
            }
        }
        catch (Exception exception)
        {
            UpdateStatusText = "No se pudo comprobar si hay actualizaciones.";
            if (!silent)
            {
                System.Windows.MessageBox.Show(
                    this,
                    exception.Message,
                    "No se pudieron buscar actualizaciones",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
        }
        finally
        {
            SetUpdateBusy(false);
            ConfigureUpdateControls();
        }
    }

    private async void UpdateTimer_Tick(object? sender, EventArgs e)
    {
        await CheckForUpdatesAsync(silent: true);
    }

    private void ConfigureUpdateControls()
    {
        if (CheckUpdatesButton is null || UpdateNowButton is null || ReleaseNotesButton is null)
        {
            return;
        }

        CheckUpdatesButton.IsEnabled = _updates.CanSelfUpdate && !_updateBusy;
        UpdateNowButton.IsEnabled = _availableUpdate is not null && !_updateBusy;
        UpdateNowButton.Visibility = _availableUpdate is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        var hasNotes = _availableUpdate is { ReleaseNotesMarkdown: not null } available &&
                       !string.IsNullOrWhiteSpace(available.ReleaseNotesMarkdown)
                       || !string.IsNullOrWhiteSpace(_updates.InstalledNotesMarkdown);
        ReleaseNotesButton.Visibility = hasNotes
            ? Visibility.Visible
            : Visibility.Collapsed;
        ReleaseNotesButton.IsEnabled = !_updateBusy;

        if (!_updateBusy)
        {
            UpdateProgressBar.Visibility = Visibility.Collapsed;
            UpdateProgressText = string.Empty;
        }
    }

    private void SetUpdateBusy(bool busy)
    {
        _updateBusy = busy;
        CheckUpdatesButton.IsEnabled = !busy && _updates.CanSelfUpdate;
        UpdateNowButton.IsEnabled = !busy && _availableUpdate is not null;
        ReleaseNotesButton.IsEnabled = !busy;
    }

    private void ShowInstalledWhatsNew()
    {
        var notes = _updates.InstalledNotesMarkdown;
        if (string.IsNullOrWhiteSpace(notes))
        {
            return;
        }

        _showWhatsNewOnNextForeground = false;
        ShowReleaseNotes(_updates.InstalledNotesVersion ?? _updates.CurrentVersion, notes);
        _updates.MarkCurrentWhatsNewSeen();
        ConfigureUpdateControls();
    }

    private void ShowReleaseNotes(string version, string? markdown)
    {
        var notesWindow = new ReleaseNotesWindow(version, markdown)
        {
            Owner = this
        };
        notesWindow.ShowDialog();
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
            _updateTimer.Stop();
            _updateTimer.Tick -= UpdateTimer_Tick;
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

    private static string FormatSessionDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1))
        {
            return "<1 s";
        }

        if (duration < TimeSpan.FromMinutes(1))
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} s";
        }

        if (duration < TimeSpan.FromHours(1))
        {
            return $"{Math.Max(1, (int)Math.Round(duration.TotalMinutes))} min";
        }

        return FormatDuration(duration);
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

    private static string FormatUpdateChannel(string channel) => channel.ToLowerInvariant() switch
    {
        "stable" => "Estable",
        "beta" => "Beta",
        "development" or "unknown" => "Desarrollo",
        _ => channel
    };

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
        public Guid GameId { get; }
        public string Title { get; }
        public string Initial { get; }
        public ImageSource? Icon { get; }
        public string FirstActivityText { get; }
        public string FirstMeasuredSessionText { get; }
        public string LastActivityText { get; }
        public string TotalText { get; }
        public string MeasuredText { get; }
        public string EstimatedText { get; }
        public int MeasuredSessionCount { get; }
        public string? ExecutablePath { get; }
        public IReadOnlyList<ActivityRowViewModel> RecentSessions { get; }

        public GameRowViewModel(DesktopGameRow game)
        {
            GameId = game.GameId;
            Title = game.Title;
            Icon = LocalGameIconService.TryLoad(game.ExecutablePath);
            Initial = Icon is null
                ? string.IsNullOrWhiteSpace(game.Title)
                    ? "?"
                    : game.Title.Trim()[..1].ToUpperInvariant()
                : string.Empty;
            FirstActivityText = FormatActivityDate(game.FirstActivityAtUtc);
            FirstMeasuredSessionText = FormatActivityDate(game.FirstMeasuredSessionAtUtc);
            LastActivityText = FormatActivityDate(game.LastActivityAtUtc);
            TotalText = FormatDuration(game.TotalPlaytime);
            MeasuredText = FormatDuration(game.MeasuredPlaytime);
            EstimatedText = game.EstimatedPlaytime > TimeSpan.Zero
                ? FormatDuration(game.EstimatedPlaytime)
                : "—";
            MeasuredSessionCount = game.MeasuredSessionCount;
            ExecutablePath = game.ExecutablePath;
            RecentSessions = game.RecentSessions
                .Select(activity => new ActivityRowViewModel(activity))
                .ToArray();
        }
    }

    public sealed class ActivityRowViewModel
    {
        public Guid GameId { get; }
        public string GameTitle { get; }
        public string WhenText { get; }
        public string DurationText { get; }
        public string ReasonText { get; }

        public ActivityRowViewModel(DesktopActivityRow activity)
        {
            GameId = activity.GameId;
            GameTitle = activity.GameTitle;
            WhenText = FormatActivityDate(activity.EndedAtUtc);
            DurationText = FormatSessionDuration(activity.Duration);
            ReasonText = FormatSessionReason(activity.EndReason);
        }

        public ActivityRowViewModel(DesktopTimelineRow activity)
        {
            GameId = activity.GameId;
            GameTitle = activity.GameTitle;

            if (activity.Kind == DesktopTimelineKind.AchievementCompleted)
            {
                var when = FormatActivityDate(activity.OccurredAtUtc);
                WhenText = activity.IsObservedTimeFallback
                    ? $"Detectado · {when}"
                    : when;
                DurationText = "100 %";
                ReasonText = activity.IsObservedTimeFallback
                    ? "★ 100 % completado · hora aproximada"
                    : "★ 100 % completado";
                return;
            }

            if (activity.Kind == DesktopTimelineKind.AchievementUnlocked)
            {
                var when = FormatActivityDate(activity.OccurredAtUtc);
                WhenText = activity.IsObservedTimeFallback
                    ? $"Detectado · {when}"
                    : when;
                DurationText = "Logro";

                var displayName = string.IsNullOrWhiteSpace(activity.AchievementDisplayName)
                    ? activity.AchievementApiName ?? "Logro desbloqueado"
                    : activity.AchievementDisplayName;
                ReasonText = activity.IsObservedTimeFallback
                    ? $"{displayName} · hora aproximada"
                    : displayName;
                return;
            }

            WhenText = FormatActivityDate(activity.OccurredAtUtc);
            DurationText = activity.Duration is TimeSpan duration
                ? FormatSessionDuration(duration)
                : "—";
            ReasonText = FormatSessionReason(activity.EndReason);
        }

        private static string FormatSessionReason(string? endReason) => endReason switch
        {
            "GracefulShutdown" => "Salida de GameHours",
            "RecoveredFromCheckpoint" => "Sesión recuperada",
            "ReconciledStop" => "Juego cerrado",
            "Stopped" => "Juego cerrado",
            null or "" => "Sesión medida",
            _ => endReason ?? "Sesión medida"
        };
    }

    public sealed record GameDetailViewModel(
        Guid GameId,
        string Title,
        string Initial,
        ImageSource? Icon,
        string LastActivityText,
        string TotalText,
        string MeasuredText,
        string EstimatedText,
        string FirstActivityText,
        string FirstMeasuredSessionText,
        string MeasuredSessionCountText,
        string ActivitySummaryText,
        string ExecutableText,
        IReadOnlyList<ActivityRowViewModel> RecentSessions);

    private enum DesktopSection
    {
        Library,
        Activity,
        Settings,
        GameDetail
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int attributeValue,
        int attributeSize);
}
