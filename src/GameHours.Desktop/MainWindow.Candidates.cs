using System.Windows;
using System.Windows.Threading;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private CandidateCenterWindow? _candidateWindow;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Dispatcher.BeginInvoke(new Action(InitializeAuxiliaryNavigation), DispatcherPriority.Loaded);
        Closed += MainWindow_CandidateFeatureClosed;
    }

    private void InitializeAuxiliaryNavigation()
    {
        LibraryNavButton.Click += StandardNavigation_Click;
        ActivityNavButton.Click += StandardNavigation_Click;
        SettingsNavButton.Click += StandardNavigation_Click;
        _host.CandidatesChanged += Host_CandidatesChanged;
        InitializeRuntimeSettings();
        _ = UpdateCandidateCountAsync();
    }

    private void Host_CandidatesChanged() => Dispatcher.BeginInvoke(new Action(async () =>
    {
        await UpdateCandidateCountAsync();
        _candidateWindow?.RequestRefresh();
    }));

    private async Task UpdateCandidateCountAsync()
    {
        try
        {
            var count = await _host.GetPendingCandidateCountAsync();
            CandidatesNavButton.Content = count == 0 ? "Pendientes" : $"Pendientes ({count})";
            CandidatesNavButton.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }
        catch
        {
            // The badge is cosmetic. Candidate persistence and tracking remain independent.
        }
    }

    private void CandidatesNav_Click(object sender, RoutedEventArgs e)
    {
        HideAnalyticsViews();
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Library);
        OpenCandidateCenter();
    }

    private void OpenCandidateCenter()
    {
        if (_candidateWindow is { IsLoaded: true })
        {
            _candidateWindow.Activate();
            return;
        }

        _candidateWindow = new CandidateCenterWindow(_host.DatabasePath) { Owner = this };
        _candidateWindow.CandidateResolved += CandidateWindow_CandidateResolved;
        _candidateWindow.Closed += CandidateWindow_Closed;
        _candidateWindow.Show();
    }

    private void CandidateWindow_CandidateResolved() => _ = RefreshAfterCandidateDecisionAsync();

    private async Task RefreshAfterCandidateDecisionAsync()
    {
        try { await _host.RefreshLibraryAsync(); }
        catch { }
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

    private void MainWindow_CandidateFeatureClosed(object? sender, EventArgs e)
    {
        Closed -= MainWindow_CandidateFeatureClosed;
        LibraryNavButton.Click -= StandardNavigation_Click;
        ActivityNavButton.Click -= StandardNavigation_Click;
        SettingsNavButton.Click -= StandardNavigation_Click;
        _host.CandidatesChanged -= Host_CandidatesChanged;
    }
}
