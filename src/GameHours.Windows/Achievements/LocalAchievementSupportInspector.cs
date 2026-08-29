namespace GameHours.Windows.Achievements;

public sealed record LocalAchievementUnavailableHint(
    string SourceText,
    string Detail);

/// <summary>
/// Explains a narrow class of local achievement failures without broadening discovery.
/// This is presentation-oriented diagnostics only: it never creates achievement state,
/// scans sibling games or changes provider semantics.
/// </summary>
public sealed class LocalAchievementSupportInspector
{
    public LocalAchievementUnavailableHint? Inspect(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var executable = Path.GetFullPath(executablePath);
            var settingsDirectory = FindSteamSettingsDirectory(executable);
            if (settingsDirectory is null || !LooksLikeGse(settingsDirectory))
            {
                return null;
            }

            if (File.Exists(Path.Combine(settingsDirectory, "achievements.json")) ||
                GseRuntimeAchievementStateLocator.TryLocate(executable) is not null)
            {
                return null;
            }

            return new LocalAchievementUnavailableHint(
                "GSE/Goldberg detectado · sin datos de logros",
                "Esta instalación usa GSE/Goldberg, pero no incluye un catálogo de logros ni ha creado un estado local de desbloqueos. GameHours no puede mostrar logros que el emulador no haya almacenado.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool LooksLikeGse(string settingsDirectory) =>
        File.Exists(Path.Combine(settingsDirectory, "configs.user.ini")) ||
        File.Exists(Path.Combine(settingsDirectory, "configs.main.ini")) ||
        File.Exists(Path.Combine(settingsDirectory, "configs.app.ini"));

    private static string? FindSteamSettingsDirectory(string executablePath)
    {
        var current = Path.GetDirectoryName(executablePath);
        for (var depth = 0; depth < 7 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            var candidate = Path.Combine(current, "steam_settings");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
    }
}
