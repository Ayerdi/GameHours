using System.Windows;
using System.Windows.Input;

namespace GameHours.Desktop;

public partial class MainWindow
{
    private bool _sessionDetailNavigationInitialized;

    private void InitializeSessionDetailNavigation()
    {
        if (_sessionDetailNavigationInitialized) return;
        _sessionDetailNavigationInitialized = true;
        ActivityView.AddHandler(
            PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(ActivityView_PreviewMouseLeftButtonUp));
    }

    private void ActivityView_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (FindActivityRow(e.OriginalSource as DependencyObject) is null)
        {
            return;
        }

        if (SessionDetailNavigation.TryOpenFromVisual(e.OriginalSource as DependencyObject, _host.DatabasePath, this))
        {
            e.Handled = true;
        }
    }

    private static ActivityRowViewModel? FindActivityRow(DependencyObject? source)
    {
        for (var current = source; current is not null; current = System.Windows.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is FrameworkElement { DataContext: ActivityRowViewModel row }) return row;
        }

        return null;
    }
}
