namespace GameHours.Windows.Achievements;

internal static class GseInstallationDetector
{
    internal static string? FindSettingsDirectory(string executablePath) =>
        SteamSettingsDirectoryLocator.FindAll(executablePath)
            .FirstOrDefault(LooksCompatibleSettingsDirectory);

    internal static bool LooksCompatibleSettingsDirectory(string settingsDirectory)
    {
        foreach (var marker in new[]
                 {
                     "configs.user.ini",
                     "configs.main.ini",
                     "configs.app.ini",
                     "steam_interfaces.txt"
                 })
        {
            if (File.Exists(Path.Combine(settingsDirectory, marker)))
            {
                return true;
            }
        }

        if (!File.Exists(Path.Combine(settingsDirectory, "steam_appid.txt")))
        {
            return false;
        }

        var parent = Directory.GetParent(settingsDirectory)?.FullName;
        return parent is not null &&
               (File.Exists(Path.Combine(parent, "steam_api.dll")) ||
                File.Exists(Path.Combine(parent, "steam_api64.dll")));
    }
}
