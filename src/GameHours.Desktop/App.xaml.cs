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

    public App() => VelopackLifecycle.Initialize();

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _host = new DesktopHost();
            await _host.InitializeAsync();
            _window = new MainWindow(_host, new WindowsStartupService(), DesktopUpdateCoordinator.CreateDefault());
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

            var background = e.Args.Any(argument => string.Equals(argument, "--background", StringComparison.OrdinalIgnoreCase));
            if (!background) _window.Show();
            _ = _window.InitializeUpdatesAsync(showWhatsNew: !background);
        }
        catch (Exception exception)
        {
            System.Windows.MessageBox.Show($"GameHours no pudo iniciarse.\n\n{exception.Message}", "GameHours", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
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
            Text = "GameHours · monitorizando",
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
