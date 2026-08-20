using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;

namespace GameHours.Desktop;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly DesktopHost _host;
    private readonly WindowsStartupService _startupService;
    private bool _allowClose;
    private bool _initializingStartup;
    private string _statusText = "Preparando…";
    private string _activeGameText = "Ningún juego activo";
    private string _activeGameSubtitle = "GameHours está esperando en segundo plano.";
    private string _gameCountText = "0";
    private string _totalPlaytimeText = "0 h";

    public ObservableCollection<GameRowViewModel> Games { get; } = new();

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

        InitializeComponent();
        DataContext = this;

        _host.StatusChanged += OnStatusChanged;

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

        if (string.IsNullOrWhiteSpace(status.ActiveGameTitle))
        {
            ActiveGameText = "Ningún juego activo";
            ActiveGameSubtitle = status.IsTracking
                ? "GameHours está monitorizando en segundo plano."
                : "El tracker está detenido.";
        }
        else
        {
            ActiveGameText = status.ActiveGameTitle;
            ActiveGameSubtitle = "La sesión se está guardando localmente.";
        }

        Games.Clear();
        foreach (var game in status.Games)
        {
            Games.Add(new GameRowViewModel(game));
        }

        GameCountText = status.Games.Count.ToString();
        var totalTicks = status.Games.Aggregate(
            0L,
            (total, game) => checked(total + game.TotalPlaytime.Ticks));
        TotalPlaytimeText = FormatDuration(TimeSpan.FromTicks(totalTicks));
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
                "No se pudo actualizar la biblioteca",
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
        public string TotalText { get; }
        public string BreakdownText { get; }

        public GameRowViewModel(DesktopGameRow game)
        {
            Title = game.Title;
            TotalText = FormatDuration(game.TotalPlaytime);

            var measured = FormatDuration(game.MeasuredPlaytime);
            var estimated = FormatDuration(game.EstimatedPlaytime);
            BreakdownText = game.EstimatedPlaytime > TimeSpan.Zero
                ? $"medido {measured} · estimado {estimated}"
                : $"medido {measured}";
        }
    }
}
