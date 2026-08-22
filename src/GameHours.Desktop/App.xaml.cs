using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using GameHours.Core.Updates;
using GameHours.Update;
using Forms = System.Windows.Forms;

namespace GameHours.Desktop;

public partial class App : System.Windows.Application
{
    private const int AchievementBalloonMaxLength = 220;
    private readonly CancellationTokenSource _startupCancellation = new();
    private DesktopHost? _host;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _openUpdatesFromTrayBalloon;
    private bool _startupFirstInputRecorded;

    public App()
    {
        StartupTrace.Mark("App constructor entered");
        VelopackLifecycle.Initialize();
        StartupTrace.Mark("VelopackLifecycle.Initialize completed");
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        var startupTrace = e.Args.Any(argument =>
            string.Equals(argument, "--startup-trace", StringComparison.OrdinalIgnoreCase));
        if (startupTrace)
        {
            StartupTrace.Enable();
        }

        StartupTrace.Mark("OnStartup entered");
        base.OnStartup(e);
        StartupTrace.Mark("base.OnStartup completed");

        var background = e.Args.Any(argument =>
            string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
        StartupWindow? startupWindow = null;

        try
        {
            // Put a lightweight, responsive surface on screen before any database or launcher
            // discovery work starts. This gives WPF a first frame and keeps the dispatcher free
            // while the real host prepares on worker threads.
            if (!background)
            {
                StartupTrace.Mark("StartupWindow construction begin");
                startupWindow = new StartupWindow();
                StartupTrace.Mark("StartupWindow construction end");
                startupWindow.Closed += StartupWindow_Closed;
                startupWindow.Loaded += (_, _) => StartupTrace.Mark("StartupWindow Loaded");
                startupWindow.ContentRendered += (_, _) => StartupTrace.Mark("StartupWindow ContentRendered");
                MainWindow = startupWindow;
                startupWindow.Show();
                StartupTrace.Mark("StartupWindow.Show returned");
                await Dispatcher.InvokeAsync(static () => { }, DispatcherPriority.ApplicationIdle);
                StartupTrace.Mark("StartupWindow first ApplicationIdle");
            }

            StartupTrace.Mark("DesktopHost construction begin");
            _host = new DesktopHost();
            StartupTrace.Mark("DesktopHost construction end");
            StartupTrace.Mark("DesktopHost.InitializeAsync begin");
            await Task.Run(
                () => _host.InitializeAsync(_startupCancellation.Token),
                _startupCancellation.Token);
            StartupTrace.Mark("DesktopHost.InitializeAsync end");

            if (_startupCancellation.IsCancellationRequested || _exiting)
            {
                return;
            }

            startupWindow?.SetStatus("Iniciando monitorización…");

            StartupTrace.Mark("MainWindow construction begin");
            _window = new MainWindow(
                _host,
                new WindowsStartupService(),
                DesktopUpdateCoordinator.CreateDefault());
            StartupTrace.Mark("MainWindow construction end");
            _window.Loaded += (_, _) => StartupTrace.Mark("MainWindow Loaded");
            _window.ContentRendered += (_, _) => StartupTrace.Mark("MainWindow ContentRendered");
            _window.Activated += (_, _) => StartupTrace.Mark("MainWindow Activated");
            _window.PreviewMouseDown += MainWindow_PreviewMouseDown;

            StartupTrace.Mark("MainWindow.ApplyInitialStatus #1 begin");
            _window.ApplyInitialStatus(_host.CurrentStatus);
            StartupTrace.Mark("MainWindow.ApplyInitialStatus #1 end");
            _window.ExitRequested += ExitApplicationAsync;
            _window.UpdateRestartRequested += ExitApplicationAsync;
            _window.UpdateAvailable += ShowUpdateAvailable;
            MainWindow = _window;

            StartupTrace.Mark("CreateTrayIcon begin");
            CreateTrayIcon();
            StartupTrace.Mark("CreateTrayIcon end");
            _host.StatusChanged += UpdateTrayStatus;
            _host.AchievementUnlocked += ShowAchievementUnlocked;

            StartupTrace.Mark("DesktopHost.StartAsync begin");
            await _host.StartAsync();
            StartupTrace.Mark("DesktopHost.StartAsync end");
            StartupTrace.Mark("MainWindow.ApplyInitialStatus #2 begin");
            _window.ApplyInitialStatus(_host.CurrentStatus);
            StartupTrace.Mark("MainWindow.ApplyInitialStatus #2 end");

            if (!background)
            {
                StartupTrace.Mark("MainWindow.Show begin");
                _window.Show();
                StartupTrace.Mark("MainWindow.Show returned");

                Dispatcher.BeginInvoke(
                    DispatcherPriority.Input,
                    new Action(() => StartupTrace.Mark("Dispatcher Input callback after MainWindow.Show")));
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ContextIdle,
                    new Action(() => StartupTrace.Mark("Dispatcher ContextIdle callback after MainWindow.Show")));
                Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => StartupTrace.Mark("Dispatcher ApplicationIdle callback after MainWindow.Show")));
            }

            if (startupWindow is not null)
            {
                startupWindow.Closed -= StartupWindow_Closed;
                startupWindow.Close();
                StartupTrace.Mark("StartupWindow closed after MainWindow.Show");
            }

            StartupTrace.Mark("InitializeUpdatesAsync invocation begin");
            _ = _window.InitializeUpdatesAsync(showWhatsNew: !background);
            StartupTrace.Mark("InitializeUpdatesAsync returned to caller");
            _ = FlushStartupTraceAfterDelayAsync();
        }
        catch (OperationCanceledException) when (
            _startupCancellation.IsCancellationRequested || _exiting)
        {
            StartupTrace.Mark("Startup cancelled");
            await StartupTrace.FlushAsync();
            if (!_exiting)
            {
                Shutdown();
            }
        }
        catch (Exception exception)
        {
            StartupTrace.Mark($"Startup failed: {exception.GetType().Name}: {exception.Message}");
            await StartupTrace.FlushAsync();
            if (startupWindow is not null)
            {
                startupWindow.Closed -= StartupWindow_Closed;
                startupWindow.Close();
            }

            System.Windows.MessageBox.Show(
                $"GameHours no pudo iniciarse.\n\n{exception.Message}",
                "GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void MainWindow_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_startupFirstInputRecorded)
        {
            return;
        }

        _startupFirstInputRecorded = true;
        StartupTrace.Mark("First MainWindow mouse input dispatched");
        if (_window is not null)
        {
            _window.PreviewMouseDown -= MainWindow_PreviewMouseDown;
        }

        _ = StartupTrace.FlushAsync();
    }

    private static async Task FlushStartupTraceAfterDelayAsync()
    {
        if (!StartupTrace.IsEnabled)
        {
            return;
        }

        await Task.Delay(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        StartupTrace.Mark("5 second post-show trace checkpoint");
        await StartupTrace.FlushAsync().ConfigureAwait(false);
    }

    private void StartupWindow_Closed(object? sender, EventArgs e)
    {
        // If the user closes the startup surface before MainWindow exists, treat that as a real
        // cancellation instead of continuing initialization invisibly in the background.
        if (_window is not null || _exiting)
        {
            return;
        }

        StartupTrace.Mark("StartupWindow closed by user; cancelling startup");
        _startupCancellation.Cancel();
        Shutdown();
    }

    private void CreateTrayIcon()
    {
        if (_window is null) return;
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Item("Abrir GameHours", () => _window.ShowFromTray()));
        menu.Items.Add(Item("Calendario de actividad…", _window.ShowCalendarFromTray));
        menu.Items.Add(Item("Estadísticas…", _window.ShowStatisticsFromTray));
        menu.Items.Add(Item("Recuperar historial de Windows…", OpenSrumHistory));
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exit = new Forms.ToolStripMenuItem("Salir");
        exit.Click += async (_, _) => await Dispatcher.InvokeAsync(ExitApplicationAsync);
        menu.Items.Add(exit);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameHours · preparando",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(_window.ShowFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(HandleTrayBalloonClick);
    }

    private Forms.ToolStripMenuItem Item(string text, Action action)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => Dispatcher.Invoke(action);
        return item;
    }

    private void HandleTrayBalloonClick()
    {
        if (_window is null) return;
        if (_openUpdatesFromTrayBalloon)
        {
            _openUpdatesFromTrayBalloon = false;
            _window.ShowUpdateSettingsFromTray();
        }
        else _window.ShowFromTray();
    }

    private void OpenSrumHistory()
    {
        if (_window is null || _host is null || _exiting) return;
        _window.ShowFromTray();
        new SrumHistoryWindow(_host.DatabasePath) { Owner = _window }.ShowDialog();
        _ = RefreshLibraryAfterSrumWindowAsync();
    }

    private async Task RefreshLibraryAfterSrumWindowAsync()
    {
        if (_host is null || _exiting) return;
        try { await _host.RefreshLibraryAsync(); } catch { }
    }

    private void UpdateTrayStatus(DesktopStatus status)
    {
        if (_trayIcon is null) return;
        Dispatcher.Invoke(() =>
        {
            if (_trayIcon is null) return;
            var text = status.ActiveGameTitle is not null
                ? $"GameHours · {status.ActiveGameTitle}"
                : status.IsTracking ? "GameHours · monitorizando" : "GameHours · detenido";
            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        });
    }

    private void ShowAchievementUnlocked(DesktopAchievementUnlocked notice)
    {
        if (_trayIcon is null || _exiting) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon is null || _exiting) return;
            _openUpdatesFromTrayBalloon = false;
            _trayIcon.ShowBalloonTip(7000, "GameHours · logro desbloqueado", BuildAchievementBalloonText(notice), Forms.ToolTipIcon.Info);
        }));
    }

    private static string BuildAchievementBalloonText(DesktopAchievementUnlocked notice)
    {
        var displayName = NormalizeNotificationText(string.IsNullOrWhiteSpace(notice.Achievement.DisplayName) ? notice.Achievement.ApiName : notice.Achievement.DisplayName);
        var lines = new List<string> { displayName };
        var description = NormalizeNotificationText(notice.Achievement.Description);
        if (!string.IsNullOrWhiteSpace(description)) lines.Add(description);
        lines.Add(NormalizeNotificationText(notice.GameTitle));
        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= AchievementBalloonMaxLength ? text : text[..(AchievementBalloonMaxLength - 1)].TrimEnd() + "…";
    }

    private static string NormalizeNotificationText(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(" ", value.Split(new[] { '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private void ShowUpdateAvailable(AppUpdate update)
    {
        if (_trayIcon is null || _exiting) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon is null || _exiting) return;
            _openUpdatesFromTrayBalloon = true;
            _trayIcon.ShowBalloonTip(8000, "GameHours · actualización disponible", $"La versión {update.Version} está lista para descargar desde Ajustes.", Forms.ToolTipIcon.Info);
        }));
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _startupCancellation.Cancel();
        try
        {
            if (_trayIcon is not null) _trayIcon.Visible = false;
            if (_host is not null)
            {
                await _host.DisposeAsync();
                _host = null;
            }
            if (_window is not null)
            {
                _window.AllowClose();
                _window.Close();
            }
            _trayIcon?.Dispose();
            _trayIcon = null;
            await StartupTrace.FlushAsync();
            Shutdown();
        }
        catch (Exception exception)
        {
            _exiting = false;
            System.Windows.MessageBox.Show(_window, $"No se pudo cerrar GameHours limpiamente.\n\n{exception.Message}", "GameHours", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        StartupTrace.Mark("Application OnExit");
        if (_host is not null)
        {
            _host.StatusChanged -= UpdateTrayStatus;
            _host.AchievementUnlocked -= ShowAchievementUnlocked;
        }
        if (_window is not null)
        {
            _window.PreviewMouseDown -= MainWindow_PreviewMouseDown;
            _window.ExitRequested -= ExitApplicationAsync;
            _window.UpdateRestartRequested -= ExitApplicationAsync;
            _window.UpdateAvailable -= ShowUpdateAvailable;
        }
        _trayIcon?.Dispose();
        StartupTrace.FlushBestEffort();
        _startupCancellation.Dispose();
        base.OnExit(e);
    }
}
