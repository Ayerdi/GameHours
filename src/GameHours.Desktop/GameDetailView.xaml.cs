using System.Windows;
using System.Windows.Controls;

namespace GameHours.Desktop;

public partial class GameDetailView : UserControl
{
    public event EventHandler? BackRequested;

    public GameDetailView()
    {
        InitializeComponent();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }
}
