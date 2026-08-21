namespace GameHours.Desktop;

public partial class MainWindow
{
    public void ShowUpdateSettingsFromTray()
    {
        ShowFromTray();
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Settings);
    }
}
