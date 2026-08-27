namespace GameHours.Desktop;

internal static class SrumSourcePathResolver
{
    public static string Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("GAMEHOURS_SRUM_PATH");
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim());
        }

        var candidates = BuildCandidates(
            Environment.SystemDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetEnvironmentVariable("WINDIR"));

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        if (candidates.Length > 0)
        {
            return candidates[0];
        }

        throw new FileNotFoundException(
            "No se pudo resolver el directorio del sistema de Windows para localizar SRUDB.dat.");
    }

    internal static string? ResolveFromCandidates(
        string? overridePath,
        string? systemDirectory,
        string? windowsDirectory,
        string? windirEnvironment,
        Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(fileExists);

        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(overridePath.Trim());
        }

        var candidates = BuildCandidates(
            systemDirectory,
            windowsDirectory,
            windirEnvironment);

        return candidates.FirstOrDefault(fileExists) ?? candidates.FirstOrDefault();
    }

    private static string[] BuildCandidates(
        string? systemDirectory,
        string? windowsDirectory,
        string? windirEnvironment) =>
        new[]
        {
            BuildFromSystemDirectory(systemDirectory),
            BuildFromWindowsDirectory(windowsDirectory),
            BuildFromWindowsDirectory(windirEnvironment)
        }
        .OfType<string>()
        .Where(path => !string.IsNullOrWhiteSpace(path))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? BuildFromSystemDirectory(string? systemDirectory) =>
        string.IsNullOrWhiteSpace(systemDirectory)
            ? null
            : Path.Combine(systemDirectory.Trim(), "sru", "SRUDB.dat");

    private static string? BuildFromWindowsDirectory(string? windowsDirectory) =>
        string.IsNullOrWhiteSpace(windowsDirectory)
            ? null
            : Path.Combine(windowsDirectory.Trim(), "System32", "sru", "SRUDB.dat");
}
