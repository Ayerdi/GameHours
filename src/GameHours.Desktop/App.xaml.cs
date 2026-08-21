using System.Windows;
using GameHours.Core.Updates;
using GameHours.Update;
using Forms = System.Windows.Forms;

namespace GameHours.Desktop;

public partial class App : System.Windows.Application
{
    private const int AchievementBalloonMaxLength = 220;

    private DesktopHost? _host;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exiting;
    private bool _openUpdatesFromTrayBalloon;

    public App()
    {
        // Velopack lifecycle hooks must run before normal desktop initialization so an
        // installed GameHours Desktop can participate in pending update operations safely.
        VelopackLifecycle.Initialize();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            _host = new DesktopHost();
            await _host.InitializeAsync();

            var startupService = new WindowsStartupService();
            var updateCoordinator = DesktopUpdateCoordinator.CreateDefault();
            _window = new MainWindow(_host, startupService, updateCoordinator);
            _window.ApplyInitialStatus(_host.CurrentStatus);
            _window.ExitRequested += ExitApplicationAsync;
            _window.UpdateRestartRequested += ExitApplicationAsync;
            _window.UpdateAvailable += ShowUpdateAvailable;
            MainWindow = _window;

            CreateTrayIcon();
            _host.StatusChanged += UpdateTrayStatus;
            _host.AchievementUnlocked += ShowAchievementUnlocked;

            await _host.StartAsync();
            _window.ApplyInitialStatus(_host.CurrentStatus);

            var startInBackground = e.Args.Any(argument =>
                string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            if (!startInBackground)
            {
                _window.Show();
            }

            _ = _window.InitializeUpdatesAsync(showWhatsNew: !startInBackground);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show(
                $"GameHours no pudo iniciarse.\n\n{exception.Message}",
                "GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void CreateTrayIcon()
    {
        if (_window is null)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        var openItem = new Forms.ToolStripMenuItem("Abrir GameHours");
        openItem.Click += (_, _) => Dispatcher.Invoke(_window.ShowFromTray);
        var calendarItem = new Forms.ToolStripMenuItem("Calendario de actividad…");
        calendarItem.Click += (_, _) => Dispatcher.Invoke(OpenActivityCalendar);
        var statisticsItem = new Forms.ToolStripMenuItem("Estadísticas…");
        statisticsItem.Click += (_, _) => Dispatcher.Invoke(OpenStatistics);
        var recoverHistoryItem = new Forms.ToolStripMenuItem("Recuperar historial de Windows…");
        recoverHistoryItem.Click += (_, _) => Dispatcher.Invoke(OpenSrumHistory);
        var exitItem = new Forms.ToolStripMenuItem("Salir");
        exitItem.Click += async (_, _) => await Dispatcher.InvokeAsync(ExitApplicationAsync);

        menu.Items.Add(openItem);
        menu.Items.Add(calendarItem);
        menu.Items.Add(statisticsItem);
        menu.Items.Add(recoverHistoryItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "GameHours · monitorizando",
            Visible = true,
            ContextMenuStrip = menu
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(_window.ShowFromTray);
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(() =>
        {
            if (_window is null)
            {
                return;
            }

            if (_openUpdatesFromTrayBalloon)
            {
                _openUpdatesFromTrayBalloon = false;
                _window.ShowUpdateSettingsFromTray();
                return;
            }

            _window.ShowFromTray();
        });
    }

    private void OpenActivityCalendar()
    {
        if (_window is null || _host is null || _exiting)
        {
            return;
        }

        _window.ShowFromTray();
        var calendarWindow = new ActivityCalendarWindow(_host.DatabasePath)
        {
            Owner = _window
        };
        calendarWindow.ShowDialog();
    }

    private void OpenStatistics()
    {
        if (_window is null || _host is null || _exiting)
        {
            return;
        }

        _window.ShowFromTray();
        var statisticsWindow = new StatisticsWindow(_host.DatabasePath)
        {
            Owner = _window
        };
        statisticsWindow.ShowDialog();
    }

    private void OpenSrumHistory()
    {
        if (_window is null || _host is null || _exiting)
        {
            return;
        }

        _window.ShowFromTray();
        var historyWindow = new SrumHistoryWindow(_host.DatabasePath)
        {
            Owner = _window
        };
        historyWindow.ShowDialog();
        _ = RefreshLibraryAfterSrumWindowAsync();
    }

    private async Task RefreshLibraryAfterSrumWindowAsync()
    {
        if (_host is null || _exiting)
        {
            return;
        }

        try
        {
            await _host.RefreshLibraryAsync();
        }
        catch
        {
            // Closing the optional history window must never affect background tracking.
        }
    }

    private void UpdateTrayStatus(DesktopStatus status)
    {
        if (_trayIcon is null)
        {
            return;
        }

        Dispatcher.Invoke(() =>
        {
            if (_trayIcon is null)
            {
                return;
            }

            var text = status.ActiveGameTitle is null
                ? "GameHours · monitorizando"
                : $"GameHours · {status.ActiveGameTitle}";
            _trayIcon.Text = text.Length <= 63 ? text : text[..63];
        });
    }

    private void ShowAchievementUnlocked(DesktopAchievementUnlocked notice)
    {
        if (_trayIcon is null || _exiting)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon is null || _exiting)
            {
                return;
            }

            _openUpdatesFromTrayBalloon = false;
            var text = BuildAchievementBalloonText(notice);

            _trayIcon.ShowBalloonTip(
                7000,
                "GameHours · logro desbloqueado",
                text,
                Forms.ToolTipIcon.Info);
        }));
    }

    private static string BuildAchievementBalloonText(DesktopAchievementUnlocked notice)
    {
        var displayName = NormalizeNotificationText(
            string.IsNullOrWhiteSpace(notice.Achievement.DisplayName)
                ? notice.Achievement.ApiName
                : notice.Achievement.DisplayName);
        var gameTitle = NormalizeNotificationText(notice.GameTitle);
        var description = NormalizeNotificationText(notice.Achievement.Description);

        var lines = new List<string>(3)
        {
            displayName
        };
        if (!string.IsNullOrWhiteSpace(description))
        {
            lines.Add(description);
        }

        lines.Add(gameTitle);
        var text = string.Join(Environment.NewLine, lines);
        return text.Length <= AchievementBalloonMaxLength
            ? text
            : text[..(AchievementBalloonMaxLength - 1)].TrimEnd() + "…";
    }

    private static string NormalizeNotificationText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private void ShowUpdateAvailable(AppUpdate update)
    {
        if (_trayIcon is null || _exiting)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon is null || _exiting)
            {
                return;
            }

            _openUpdatesFromTrayBalloon = true;
            _trayIcon.ShowBalloonTip(
                8000,
                "GameHours · actualización disponible",
                $"La versión {update.Version} está lista para descargar desde Ajustes.",
                Forms.ToolTipIcon.Info);
        }));
    }

    private async Task ExitApplicationAsync()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        try
        {
            if (_trayIcon is not null)
            {
                _trayIcon.Visible = false;
            }

            if (_host is not null)
            {
                await _host.StopAsync();
            }

            if (_window is not null)
            {
                _window.AllowClose();
                _window.Close();
            }

            _trayIcon?.Dispose();
            _trayIcon = null;
            Shutdown();
        }
        catch (Exception exception)
        {
            _exiting = false;
            System.Windows.MessageBox.Show(
                _window,
                $"No se pudo cerrar GameHours limpiamente.\n\n{exception.Message}",
                "GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_host is not null)
        {
            _host.StatusChanged -= UpdateTrayStatus;
            _host.AchievementUnlocked -= ShowAchievementUnlocked;
        }

        if (_window is not null)
        {
            _window.ExitRequested -= ExitApplicationAsync;
            _window.UpdateRestartRequested -= ExitApplicationAsync;
            _window.UpdateAvailable -= ShowUpdateAvailable;
        }

        _trayIcon?.Dispose();
        base.OnExit(e);
    }
}
