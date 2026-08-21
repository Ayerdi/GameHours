using System.Windows;

namespace GameHours.Desktop;

public partial class ReleaseNotesWindow : Window
{
    public string VersionText { get; }
    public string NotesText { get; }

    public ReleaseNotesWindow(string version, string? releaseNotesMarkdown)
    {
        VersionText = string.IsNullOrWhiteSpace(version)
            ? "Versión desconocida"
            : $"Versión {version.Trim()}";
        NotesText = ReleaseNotesFormatter.ToPlainText(releaseNotesMarkdown);

        InitializeComponent();
        DataContext = this;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
