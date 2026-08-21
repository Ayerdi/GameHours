using System.Windows;
using GameHours.Core.Updates;
using GameHours.Update;
using Forms = System.Windows.Forms;

namespace GameHours.Desktop;

public partial class App : System.Windows.Application
{
    private DesktopHost? _host;
    private MainWindow? _window;
    private Forms.NotifyIcon? _trayIcon;
    private bool _exiting;

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
        var exitItem = new Forms.ToolStripMenuItem("Salir");
        exitItem.Click += async (_, _) => await Dispatcher.InvokeAsync(ExitApplicationAsync);

        menu.Items.Add(openItem);
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
        _trayIcon.BalloonTipClicked += (_, _) => Dispatcher.Invoke(_window.ShowFromTray);
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

            var displayName = string.IsNullOrWhiteSpace(notice.Achievement.DisplayName)
                ? notice.Achievement.ApiName
                : notice.Achievement.DisplayName;
            var text = $"{notice.GameTitle}\n{displayName}";
            if (text.Length > 220)
            {
                text = text[..217] + "…";
            }

            _trayIcon.ShowBalloonTip(
                6000,
                "GameHours · logro desbloqueado",
                text,
                Forms.ToolTipIcon.Info);
        }));
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
