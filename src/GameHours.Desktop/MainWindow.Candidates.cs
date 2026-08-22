using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GameHours.Storage.Sqlite;
using WpfButton = System.Windows.Controls.Button;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private WpfButton? _candidateNavButton;
    private DesktopGameCandidateScanner? _candidateScanner;
    private CandidateCenterWindow? _candidateWindow;
    private bool _candidateFeatureStarted;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Dispatcher.BeginInvoke(
            new Action(async () => await InitializeCandidateFeatureAsync()),
            DispatcherPriority.Loaded);
        Closed += MainWindow_CandidateFeatureClosed;
    }

    private async Task InitializeCandidateFeatureAsync()
    {
        if (_candidateFeatureStarted || string.IsNullOrWhiteSpace(_host.DatabasePath))
        {
            return;
        }

        _candidateFeatureStarted = true;
        try
        {
            AddCandidateNavigationButton();

            var database = new GameHoursDatabase(_host.DatabasePath);
            await database.InitializeAsync();
            _candidateScanner = new DesktopGameCandidateScanner(database);
            _candidateScanner.CandidatesChanged += CandidateScanner_CandidatesChanged;
            await _candidateScanner.StartAsync();
            await UpdateCandidateCountAsync();
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            if (_candidateNavButton is not null)
            {
                _candidateNavButton.ToolTip = $"No se pudo iniciar la detección de candidatos: {exception.Message}";
            }
        }
    }

    private void AddCandidateNavigationButton()
    {
        if (_candidateNavButton is not null || LibraryNavButton.Parent is not StackPanel navigation)
        {
            return;
        }

        _candidateNavButton = new WpfButton
        {
            Content = "Pendientes",
            Background = Brushes.Transparent,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Revisar ejecutables que GameHours no ha identificado con suficiente confianza"
        };
        _candidateNavButton.Click += CandidatesNav_Click;

        var settingsIndex = navigation.Children.IndexOf(SettingsNavButton);
        if (settingsIndex >= 0)
        {
            navigation.Children.Insert(settingsIndex, _candidateNavButton);
        }
        else
        {
            navigation.Children.Add(_candidateNavButton);
        }
    }

    private void CandidateScanner_CandidatesChanged()
    {
        Dispatcher.BeginInvoke(new Action(async () =>
        {
            await UpdateCandidateCountAsync();
            _candidateWindow?.RequestRefresh();
        }));
    }

    private async Task UpdateCandidateCountAsync()
    {
        if (_candidateScanner is null || _candidateNavButton is null)
        {
            return;
        }

        try
        {
            var count = await _candidateScanner.GetPendingCountAsync();
            _candidateNavButton.Content = count == 0 ? "Pendientes" : $"Pendientes ({count})";
            _candidateNavButton.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }
        catch
        {
            // A badge refresh is cosmetic and must never affect tracking or the main window.
        }
    }

    private void CandidatesNav_Click(object sender, RoutedEventArgs e)
    {
        if (_candidateWindow is { IsLoaded: true })
        {
            _candidateWindow.Activate();
            return;
        }

        _candidateWindow = new CandidateCenterWindow(_host.DatabasePath)
        {
            Owner = this
        };
        _candidateWindow.CandidateResolved += CandidateWindow_CandidateResolved;
        _candidateWindow.Closed += CandidateWindow_Closed;
        _candidateWindow.Show();
    }

    private void CandidateWindow_CandidateResolved()
    {
        _ = RefreshAfterCandidateDecisionAsync();
    }

    private async Task RefreshAfterCandidateDecisionAsync()
    {
        try
        {
            await _host.RefreshLibraryAsync();
        }
        catch
        {
            // The candidate decision is already durable even if the library refresh fails.
        }

        await UpdateCandidateCountAsync();
    }

    private void CandidateWindow_Closed(object? sender, EventArgs e)
    {
        if (_candidateWindow is not null)
        {
            _candidateWindow.CandidateResolved -= CandidateWindow_CandidateResolved;
            _candidateWindow.Closed -= CandidateWindow_Closed;
            _candidateWindow = null;
        }

        _ = UpdateCandidateCountAsync();
    }

    private async void MainWindow_CandidateFeatureClosed(object? sender, EventArgs e)
    {
        Closed -= MainWindow_CandidateFeatureClosed;
        if (_candidateScanner is null)
        {
            return;
        }

        _candidateScanner.CandidatesChanged -= CandidateScanner_CandidatesChanged;
        await _candidateScanner.DisposeAsync();
        _candidateScanner = null;
    }
}
