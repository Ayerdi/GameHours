using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private bool _dataPortabilityAttached;

    static MainWindow()
    {
        EventManager.RegisterClassHandler(
            typeof(MainWindow),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler(MainWindow_PortabilityLoaded));
    }

    private static void MainWindow_PortabilityLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is MainWindow window)
        {
            window.AttachDataPortabilitySettings();
        }
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
