using System.Globalization;
using System.Text.Json;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Reads Steam's local userdata/config/librarycache achievement state.
/// The cache is treated as partial state: it is not assumed to contain the full achievement catalogue.
/// </summary>
public sealed class SteamLibraryCacheAchievementReader
{
    private readonly LocalAchievementSourceLocator _locator = new();

    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var candidates = _locator.Locate(executablePath)
                .Where(item => item.Kind == LocalAchievementSourceKind.SteamLibraryCache)
                .OrderByDescending(item => SafeLastWriteTimeUtc(item.FilePath))
                .ToArray();

            foreach (var candidate in candidates)
            {
                var snapshot = TryReadCacheFile(candidate.FilePath, candidate.AppId);
                if (snapshot is not null)
                {
                    return snapshot;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or PathTooLongException)
        {
        }

        return null;
    }

    public LocalAchievementSnapshot? TryReadCacheFile(string filePath, string? appId = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(filePath));
            if (!TryGetHighlightVector(document.RootElement, out var highlights) ||
                highlights.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var achievements = new List<LocalAchievement>();
            foreach (var item in highlights.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var apiName = ReadString(item, "strID")
                    ?? ReadString(item, "id")
                    ?? ReadString(item, "name");
                if (string.IsNullOrWhiteSpace(apiName))
                {
                    continue;
                }

                var unlocked = ReadBool(item, "bAchieved")
                    ?? ReadBool(item, "achieved")
                    ?? false;
                var unlockedAt = unlocked
                    ? ReadUnixTimestamp(item, "rtUnlocked", "unlockTime", "unlock_time")
                    : null;

                achievements.Add(new LocalAchievement(
                    apiName,
                    apiName,
                    string.Empty,
                    Hidden: false,
                    IsUnlocked: unlocked,
                    UnlockedAtUtc: unlockedAt,
                    IconPath: null,
                    LockedIconPath: null,
                    Progress: null,
                    MaxProgress: null));
            }

            if (achievements.Count == 0)
            {
                return null;
            }

            var normalized = achievements
                .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.IsUnlocked)
                    .ThenByDescending(item => item.UnlockedAtUtc)
                    .First())
                .ToArray();

            return new LocalAchievementSnapshot(
                "Steam local cache · estado parcial",
                NormalizeAppId(appId) ?? InferAppIdFromFileName(filePath),
                filePath,
                filePath,
                normalized);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    private static bool TryGetHighlightVector(JsonElement root, out JsonElement highlights)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var values = item.EnumerateArray().ToArray();
                if (values.Length < 2 ||
                    values[0].ValueKind != JsonValueKind.String ||
                    !string.Equals(values[0].GetString(), "achievements", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (TryGetHighlightVectorFromAchievementsNode(values[1], out highlights))
                {
                    return true;
                }
            }
        }
        else if (root.ValueKind == JsonValueKind.Object)
        {
            if (TryGetProperty(root, "achievements", out var achievements) &&
                TryGetHighlightVectorFromAchievementsNode(achievements, out highlights))
            {
                return true;
            }

            if (TryGetHighlightVectorFromAchievementsNode(root, out highlights))
            {
                return true;
            }
        }

        highlights = default;
        return false;
    }

    private static bool TryGetHighlightVectorFromAchievementsNode(
        JsonElement achievements,
        out JsonElement highlights)
    {
        if (achievements.ValueKind != JsonValueKind.Object)
        {
            highlights = default;
            return false;
        }

        var data = achievements;
        if (TryGetProperty(achievements, "data", out var dataProperty) &&
            dataProperty.ValueKind == JsonValueKind.Object)
        {
            data = dataProperty;
        }

        if (TryGetProperty(data, "vecHighlight", out highlights) &&
            highlights.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        highlights = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : null;
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
            JsonValueKind.String when long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number != 0,
            _ => null
        };
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, params string[] propertyNames)
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
                     long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
            {
                seconds = numeric;
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

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static DateTime SafeLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    private static string? InferAppIdFromFileName(string filePath)
    {
        var name = Path.GetFileNameWithoutExtension(filePath);
        return NormalizeAppId(name);
    }

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }
}
