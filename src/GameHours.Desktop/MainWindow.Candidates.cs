using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfButton = System.Windows.Controls.Button;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private WpfButton? _candidateNavButton;
    private CandidateCenterWindow? _candidateWindow;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Dispatcher.BeginInvoke(new Action(InitializeAnalyticsNavigation), DispatcherPriority.Loaded);
        Dispatcher.BeginInvoke(new Action(InitializeCandidateFeature), DispatcherPriority.Loaded);
        Closed += MainWindow_CandidateFeatureClosed;
    }

    private void InitializeCandidateFeature()
    {
        AddCandidateNavigationButton();
        _host.CandidatesChanged += Host_CandidatesChanged;
        _ = UpdateCandidateCountAsync();
    }

    private void AddCandidateNavigationButton()
    {
        if (_candidateNavButton is not null || LibraryNavButton.Parent is not StackPanel navigation) return;
        _candidateNavButton = new WpfButton
        {
            Content = "Pendientes",
            Background = WpfBrushes.Transparent,
            Padding = new Thickness(14, 8, 14, 8),
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Revisar ejecutables que GameHours no ha identificado con suficiente confianza"
        };
        _candidateNavButton.Click += CandidatesNav_Click;
        var settingsIndex = navigation.Children.IndexOf(SettingsNavButton);
        if (settingsIndex >= 0) navigation.Children.Insert(settingsIndex, _candidateNavButton); else navigation.Children.Add(_candidateNavButton);
    }

    private void Host_CandidatesChanged() => Dispatcher.BeginInvoke(new Action(async () =>
    {
        await UpdateCandidateCountAsync();
        _candidateWindow?.RequestRefresh();
    }));

    private async Task UpdateCandidateCountAsync()
    {
        if (_candidateNavButton is null) return;
        try
        {
            var count = await _host.GetPendingCandidateCountAsync();
            _candidateNavButton.Content = count == 0 ? "Pendientes" : $"Pendientes ({count})";
            _candidateNavButton.FontWeight = count > 0 ? FontWeights.SemiBold : FontWeights.Normal;
        }
        catch { }
    }

    private void CandidatesNav_Click(object sender, RoutedEventArgs e)
    {
        if (_candidateWindow is { IsLoaded: true }) { _candidateWindow.Activate(); return; }
        _candidateWindow = new CandidateCenterWindow(_host.DatabasePath) { Owner = this };
        _candidateWindow.CandidateResolved += CandidateWindow_CandidateResolved;
        _candidateWindow.Closed += CandidateWindow_Closed;
        _candidateWindow.Show();
    }

    private void CandidateWindow_CandidateResolved() => _ = RefreshAfterCandidateDecisionAsync();

    private async Task RefreshAfterCandidateDecisionAsync()
    {
        try { await _host.RefreshLibraryAsync(); } catch { }
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
        _host.CandidatesChanged -= Host_CandidatesChanged;
    }
}
