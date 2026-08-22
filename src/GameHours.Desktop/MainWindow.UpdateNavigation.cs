namespace GameHours.Desktop;

public partial class MainWindow
{
    public void ShowUpdateSettingsFromTray()
    {
        ShowFromTray();
        HideAnalyticsViews();
        _selectedGameId = null;
        SelectedGameDetail = null;
        ShowSection(DesktopSection.Settings);
    }
}
