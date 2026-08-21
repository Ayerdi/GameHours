using System.Windows;

namespace GameHours.Desktop;

public partial class GameDetailView : System.Windows.Controls.UserControl
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
