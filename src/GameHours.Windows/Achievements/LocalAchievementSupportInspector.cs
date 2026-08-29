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
            var settingsDirectory = GseInstallationDetector.FindSettingsDirectory(executable);
            if (settingsDirectory is null)
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
                "Esta instalación usa GSE/Goldberg, pero no incluye un catálogo de logros ni ha creado un estado local de desbloqueos. GameHours puede intentar preparar el catálogo sin inventar desbloqueos.");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }
}
