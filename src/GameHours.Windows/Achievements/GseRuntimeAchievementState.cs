using System.Globalization;
using System.Text.Json;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Reads GSE/Goldberg runtime achievement state independently from the local catalogue.
/// Modern GSE can redirect saves through configs.user.ini, so state discovery must follow
/// the emulator configuration instead of assuming a fixed AppData folder.
/// </summary>
internal sealed class GseRuntimeAchievementStateReader
{
    public LocalAchievementSnapshot? TryRead(string executablePath, string? appIdHint = null)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var location = GseRuntimeAchievementStateLocator.TryLocate(executablePath, appIdHint);
            if (location is null)
            {
                return null;
            }

            var achievements = ReadUnlockedAchievements(location.FilePath);
            return new LocalAchievementSnapshot(
                "GSE/Goldberg local · estado parcial",
                location.AppId,
                location.FilePath,
                location.FilePath,
                achievements)
            {
                IsCatalogueComplete = false
            };
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<LocalAchievement> ReadUnlockedAchievements(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<LocalAchievement>();

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (TryReadUnlockedState(property.Value, out var unlockedAt))
                {
                    results.Add(CreateUnlocked(property.Name, unlockedAt));
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !TryReadUnlockedState(item, out var unlockedAt))
                {
                    continue;
                }

                var apiName = ReadString(item, "name")
                    ?? ReadString(item, "apiname")
                    ?? ReadString(item, "api_name");
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    results.Add(CreateUnlocked(apiName, unlockedAt));
                }
            }
        }

        return results
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .OrderBy(item => item.UnlockedAtUtc ?? DateTimeOffset.MaxValue)
                .First())
            .OrderBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static LocalAchievement CreateUnlocked(string apiName, DateTimeOffset? unlockedAtUtc) =>
        new(
            apiName,
            apiName,
            string.Empty,
            Hidden: false,
            IsUnlocked: true,
            UnlockedAtUtc: unlockedAtUtc,
            IconPath: null,
            LockedIconPath: null,
            Progress: null,
            MaxProgress: null);

    private static bool TryReadUnlockedState(JsonElement element, out DateTimeOffset? unlockedAtUtc)
    {
        unlockedAtUtc = null;

        if (element.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out var numeric))
        {
            return numeric != 0;
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var earned = ReadBool(element, "earned")
            ?? ReadBool(element, "Achieved")
            ?? ReadBool(element, "achieved")
            ?? false;
        if (!earned)
        {
            return false;
        }

        unlockedAtUtc = ReadTimestamp(
            element,
            "earned_time",
            "UnlockTime",
            "unlocktime",
            "unlock_time");
        return true;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(
                value.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var number) => number != 0,
            _ => null
        };
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var value))
            {
                continue;
            }

            long seconds;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numeric))
            {
                seconds = numeric;
            }
            else if (value.ValueKind == JsonValueKind.String &&
                     long.TryParse(
                         value.GetString(),
                         NumberStyles.Integer,
                         CultureInfo.InvariantCulture,
                         out var parsed))
            {
                seconds = parsed;
            }
            else
            {
                continue;
            }

            if (seconds <= 0)
            {
                continue;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        TryGetProperty(element, propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }
}

internal sealed record GseRuntimeAchievementStateLocation(string AppId, string FilePath);

internal static class GseRuntimeAchievementStateLocator
{
    public static GseRuntimeAchievementStateLocation? TryLocate(
        string executablePath,
        string? appIdHint = null)
    {
        var executable = Path.GetFullPath(executablePath);
        var settingsDirectory = FindSteamSettingsDirectory(executable);
        var appId = NormalizeAppId(appIdHint) ?? TryReadAppId(executable, settingsDirectory);
        if (appId is null)
        {
            return null;
        }

        foreach (var candidate in EnumerateStatePaths(executable, settingsDirectory, appId))
        {
            if (File.Exists(candidate))
            {
                return new GseRuntimeAchievementStateLocation(appId, Path.GetFullPath(candidate));
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateStatePaths(
        string executablePath,
        string? settingsDirectory,
        string appId)
    {
        if (settingsDirectory is not null)
        {
            var userConfig = Path.Combine(settingsDirectory, "configs.user.ini");
            var localSavePath = ReadIniValue(userConfig, "local_save_path");
            if (!string.IsNullOrWhiteSpace(localSavePath))
            {
                var dllDirectory = Directory.GetParent(settingsDirectory)?.FullName
                    ?? Path.GetDirectoryName(executablePath)
                    ?? settingsDirectory;
                var saveRoot = Path.IsPathFullyQualified(localSavePath)
                    ? Path.GetFullPath(localSavePath)
                    : Path.GetFullPath(Path.Combine(
                        dllDirectory,
                        localSavePath.Replace('/', Path.DirectorySeparatorChar)));

                foreach (var candidate in GseAchievementStatePathLocator.FindExistingInAppDirectory(
                             Path.Combine(saveRoot, appId)))
                {
                    yield return candidate;
                }
                yield break;
            }

            var savesFolderName = ReadIniValue(userConfig, "saves_folder_name");
            if (!string.IsNullOrWhiteSpace(savesFolderName))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrWhiteSpace(appData))
                {
                    foreach (var candidate in GseAchievementStatePathLocator.FindExistingInAppDirectory(
                                 Path.Combine(appData, savesFolderName, appId)))
                    {
                        yield return candidate;
                    }
                }
                yield break;
            }

            // Some portable layouts keep the per-app state below steam_settings itself.
            foreach (var candidate in GseAchievementStatePathLocator.FindExistingInAppDirectory(
                         Path.Combine(settingsDirectory, appId)))
            {
                yield return candidate;
            }
        }

        foreach (var candidate in GseAchievementStatePathLocator.FindExisting(appId))
        {
            yield return candidate;
        }
    }

    internal static string? FindSteamSettingsDirectory(string executablePath) =>
        SteamSettingsDirectoryLocator.FindNearest(executablePath);

    internal static string? TryReadAppId(string executablePath, string? settingsDirectory)
    {
        if (settingsDirectory is not null)
        {
            var value = NormalizeAppId(ReadTextFile(Path.Combine(settingsDirectory, "steam_appid.txt")));
            if (value is not null)
            {
                return value;
            }
        }

        foreach (var root in EnumerateAncestorDirectories(executablePath, maxDepth: 7))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "steam_appid.txt"),
                         Path.Combine(root, "steam_settings", "steam_appid.txt")
                     })
            {
                var value = NormalizeAppId(ReadTextFile(candidate));
                if (value is not null)
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? ReadIniValue(string path, string key)
    {
        var text = ReadTextFile(path);
        if (text is null)
        {
            return null;
        }

        foreach (var rawLine in text.TrimStart('\ufeff').Split(
                     new[] { "\r\n", "\n", "\r" },
                     StringSplitOptions.None))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';') || line.StartsWith('['))
            {
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0 ||
                !line[..separator].Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = line[(separator + 1)..].Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }

        return null;
    }

    private static string? ReadTextFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return File.ReadAllText(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private static IEnumerable<string> EnumerateAncestorDirectories(string executablePath, int maxDepth)
    {
        var current = Path.GetDirectoryName(executablePath);
        for (var depth = 0; depth < maxDepth && !string.IsNullOrWhiteSpace(current); depth++)
        {
            yield return current;

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase))
            {
                yield break;
            }

            current = parent;
        }
    }
}
