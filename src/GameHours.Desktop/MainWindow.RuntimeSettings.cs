using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private RuntimeSettingsCard? _runtimeSettingsCard;
    private RuntimeDiagnosticsWindow? _runtimeDiagnosticsWindow;

    private void InitializeRuntimeSettings()
    {
        if (_runtimeSettingsCard is not null)
        {
            return;
        }

        if (SettingsView.Child is not ScrollViewer scrollViewer ||
            scrollViewer.Content is not StackPanel settingsPanel)
        {
            return;
        }

        var card = new RuntimeSettingsCard(_host)
        {
            Margin = new Thickness(0, 0, 0, 12)
        };
        card.DiagnosticsRequested += RuntimeSettings_DiagnosticsRequested;
        card.ExecutableManagementRequested += RuntimeSettings_ExecutableManagementRequested;

        // Keep the title/subtitle first, then put runtime/privacy controls before the more
        // administrative startup/update/data sections.
        settingsPanel.Children.Insert(Math.Min(2, settingsPanel.Children.Count), card);
        _runtimeSettingsCard = card;

        _host.StatusChanged += RuntimeSettings_HostStatusChanged;
        _host.PreferencesChanged += RuntimeSettings_HostPreferencesChanged;
        Closed += MainWindow_RuntimeSettingsClosed;
        ApplyLowImpactUpdatePolicy(_host.CurrentStatus, _host.Preferences);
    }

    private void RuntimeSettings_DiagnosticsRequested()
    {
        if (_runtimeDiagnosticsWindow is { IsLoaded: true })
        {
            _runtimeDiagnosticsWindow.Activate();
            return;
        }

        _runtimeDiagnosticsWindow = new RuntimeDiagnosticsWindow(_host) { Owner = this };
        _runtimeDiagnosticsWindow.Closed += RuntimeDiagnosticsWindow_Closed;
        _runtimeDiagnosticsWindow.Show();
    }

    private void RuntimeSettings_ExecutableManagementRequested() => OpenCandidateCenter();

    private void RuntimeSettings_HostStatusChanged(DesktopStatus status)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
                ApplyLowImpactUpdatePolicy(status, _host.Preferences)));
            return;
        }

        ApplyLowImpactUpdatePolicy(status, _host.Preferences);
    }

    private void RuntimeSettings_HostPreferencesChanged(DesktopPreferences preferences)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() =>
                ApplyLowImpactUpdatePolicy(_host.CurrentStatus, preferences)));
            return;
        }

        ApplyLowImpactUpdatePolicy(_host.CurrentStatus, preferences);
    }

    private void ApplyLowImpactUpdatePolicy(
        DesktopStatus status,
        DesktopPreferences preferences)
    {
        if (!_updates.CanSelfUpdate)
        {
            _updateTimer.Stop();
            return;
        }

        if (preferences.LowImpactMode && status.ActiveGameTitle is not null)
        {
            _updateTimer.Stop();
            return;
        }

        if (!_updateTimer.IsEnabled)
        {
            _updateTimer.Start();
        }
    }

    private void RuntimeDiagnosticsWindow_Closed(object? sender, EventArgs e)
    {
        if (_runtimeDiagnosticsWindow is not null)
        {
            _runtimeDiagnosticsWindow.Closed -= RuntimeDiagnosticsWindow_Closed;
            _runtimeDiagnosticsWindow = null;
        }
    }

    private void MainWindow_RuntimeSettingsClosed(object? sender, EventArgs e)
    {
        Closed -= MainWindow_RuntimeSettingsClosed;
        _host.StatusChanged -= RuntimeSettings_HostStatusChanged;
        _host.PreferencesChanged -= RuntimeSettings_HostPreferencesChanged;

        if (_runtimeSettingsCard is not null)
        {
            _runtimeSettingsCard.DiagnosticsRequested -= RuntimeSettings_DiagnosticsRequested;
            _runtimeSettingsCard.ExecutableManagementRequested -= RuntimeSettings_ExecutableManagementRequested;
            _runtimeSettingsCard = null;
        }

        if (_runtimeDiagnosticsWindow is not null)
        {
            _runtimeDiagnosticsWindow.Closed -= RuntimeDiagnosticsWindow_Closed;
            _runtimeDiagnosticsWindow.Close();
            _runtimeDiagnosticsWindow = null;
        }
    }
}
