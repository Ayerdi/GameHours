using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private bool _dataPortabilityAttached;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += MainWindow_PortabilityLoaded;
    }

    private void MainWindow_PortabilityLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_PortabilityLoaded;
        AttachDataPortabilitySettings();
    }

    private void AttachDataPortabilitySettings()
    {
        if (_dataPortabilityAttached || string.IsNullOrWhiteSpace(_host.DatabasePath))
        {
            return;
        }

        if (SettingsView.Child is not ScrollViewer { Content: StackPanel settingsPanel })
        {
            return;
        }

        var card = new DataPortabilitySettingsCard(_host.DatabasePath);
        var insertionIndex = Math.Max(0, settingsPanel.Children.Count - 1);
        settingsPanel.Children.Insert(insertionIndex, card);
        _dataPortabilityAttached = true;
    }
}
