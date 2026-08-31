using System.Globalization;
using System.Text.Json;

namespace GameHours.Windows.Achievements;

public sealed record LocalAchievement(
    string ApiName,
    string DisplayName,
    string Description,
    bool Hidden,
    bool IsUnlocked,
    DateTimeOffset? UnlockedAtUtc,
    string? IconPath,
    string? LockedIconPath,
    long? Progress,
    long? MaxProgress);

public sealed record LocalAchievementSnapshot(
    string Source,
    string? AppId,
    string DefinitionPath,
    string? StatePath,
    IReadOnlyList<LocalAchievement> Achievements)
{
    public int UnlockedCount => Achievements.Count(item => item.IsUnlocked);
    public bool IsCatalogueComplete { get; init; } = true;
}

public sealed class GseAchievementReader
{
    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var settingsDirectory = FindSteamSettingsDirectory(executablePath);
            if (settingsDirectory is null)
            {
                return null;
            }

            var definitionPath = Path.Combine(settingsDirectory, "achievements.json");
            if (!File.Exists(definitionPath))
            {
                return null;
            }

            var appId = GseRuntimeAchievementStateLocator.TryReadAppId(executablePath, settingsDirectory);
            var statePath = string.IsNullOrWhiteSpace(appId)
                ? null
                : FindStatePath(appId);

            var definitions = ReadDefinitions(definitionPath, settingsDirectory);
            if (definitions.Count == 0)
            {
                return null;
            }

            var state = statePath is null
                ? new Dictionary<string, AchievementState>(StringComparer.OrdinalIgnoreCase)
                : ReadState(statePath);

            var achievements = definitions
                .Select(definition =>
                {
                    state.TryGetValue(definition.ApiName, out var itemState);
                    var unlocked = itemState?.Earned == true;
                    return new LocalAchievement(
                        definition.ApiName,
                        definition.DisplayName,
                        definition.Description,
                        definition.Hidden,
                        unlocked,
                        unlocked ? itemState?.UnlockedAtUtc : null,
                        definition.IconPath,
                        definition.LockedIconPath,
                        itemState?.Progress,
                        itemState?.MaxProgress ?? definition.MaxProgress);
                })
                .ToArray();

            return new LocalAchievementSnapshot(
                "GSE/Goldberg local",
                appId,
                definitionPath,
                statePath,
                achievements);
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? FindSteamSettingsDirectory(string executablePath) =>
        SteamSettingsDirectoryLocator.FindAll(executablePath)
            .FirstOrDefault(directory => File.Exists(Path.Combine(directory, "achievements.json")));

    private static string? FindStatePath(string appId)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming))
        {
            return null;
        }

        foreach (var candidate in new[]
                 {
                     Path.Combine(roaming, "GSE Saves", appId, "achievements.json"),
                     Path.Combine(roaming, "Goldberg SteamEmu Saves", appId, "achievements.json")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<AchievementDefinition> ReadDefinitions(
        string definitionPath,
        string settingsDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(definitionPath));
        var results = new List<AchievementDefinition>();

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                var definition = ParseDefinition(item, null, settingsDirectory);
                if (definition is not null)
                {
                    results.Add(definition);
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                var definition = ParseDefinition(property.Value, property.Name, settingsDirectory);
                if (definition is not null)
                {
                    results.Add(definition);
                }
            }
        }

        return results
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    private static AchievementDefinition? ParseDefinition(
        JsonElement element,
        string? fallbackApiName,
        string settingsDirectory)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var apiName = ReadString(element, "name") ?? fallbackApiName;
        if (string.IsNullOrWhiteSpace(apiName))
        {
            return null;
        }

        var displayName = ReadLocalizedString(element, "displayName")
            ?? ReadString(element, "display_name")
            ?? apiName;
        var description = ReadLocalizedString(element, "description")
            ?? ReadString(element, "desc")
            ?? string.Empty;
        var hidden = ReadBool(element, "hidden") ?? false;
        var icon = ReadString(element, "icon");
        var lockedIcon = ReadString(element, "icongray")
            ?? ReadString(element, "icon_gray");
        var maxProgress = ReadLong(element, "max_progress");

        return new AchievementDefinition(
            apiName,
            displayName,
            description,
            hidden,
            ResolveImagePath(settingsDirectory, icon),
            ResolveImagePath(settingsDirectory, lockedIcon),
            maxProgress);
    }

    private static Dictionary<string, AchievementState> ReadState(string statePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(statePath));
        var results = new Dictionary<string, AchievementState>(StringComparer.OrdinalIgnoreCase);

        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                results[property.Name] = ParseStateItem(property.Value);
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var apiName = ReadString(item, "name")
                    ?? ReadString(item, "apiname")
                    ?? ReadString(item, "api_name");
                if (!string.IsNullOrWhiteSpace(apiName))
                {
                    results[apiName] = ParseStateItem(item);
                }
            }
        }

        return results;
    }

    private static AchievementState ParseStateItem(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return new AchievementState(element.GetBoolean(), null, null, null);
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return new AchievementState(false, null, null, null);
        }

        var earned = ReadBool(element, "earned")
            ?? ReadBool(element, "Achieved")
            ?? ReadBool(element, "achieved")
            ?? false;
        var unlockedAt = ReadUnixTimestamp(
            element,
            "earned_time",
            "UnlockTime",
            "unlocktime",
            "unlock_time");
        var progress = ReadLong(element, "progress");
        var maxProgress = ReadLong(element, "max_progress");

        return new AchievementState(earned, unlockedAt, progress, maxProgress);
    }

    private static string? ReadLocalizedString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }

        if (value.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var preferred = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "es" => new[] { "spanish", "spanish - spain", "spanish_spain", "latam", "english" },
            "pt" => new[] { "brazilian", "portuguese", "english" },
            "fr" => new[] { "french", "english" },
            "de" => new[] { "german", "english" },
            "it" => new[] { "italian", "english" },
            _ => new[] { "english" }
        };

        foreach (var language in preferred)
        {
            if (TryGetProperty(value, language, out var localized) &&
                localized.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(localized.GetString()))
            {
                return localized.GetString();
            }
        }

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(property.Value.GetString()))
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static bool? ReadBool(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue))
        {
            return numericValue != 0;
        }

        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (bool.TryParse(text, out var parsedBool))
            {
                return parsedBool;
            }

            if (long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedNumber))
            {
                return parsedNumber != 0;
            }
        }

        return null;
    }

    private static long? ReadLong(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var numericValue))
        {
            return numericValue;
        }

        if (value.ValueKind == JsonValueKind.String &&
            long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedValue))
        {
            return parsedValue;
        }

        return null;
    }

    private static DateTimeOffset? ReadUnixTimestamp(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var seconds = ReadLong(element, propertyName);
            if (seconds is null || seconds <= 0)
            {
                continue;
            }

            try
            {
                return DateTimeOffset.FromUnixTimeSeconds(seconds.Value);
            }
            catch (ArgumentOutOfRangeException)
            {
            }
        }

        return null;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static string? ResolveImagePath(string settingsDirectory, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return null; // Local-only reader intentionally does not fetch remote artwork.
        }

        var normalized = value.Replace('/', Path.DirectorySeparatorChar);
        var candidate = Path.IsPathFullyQualified(normalized)
            ? normalized
            : Path.Combine(settingsDirectory, normalized);

        if (File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        var fileName = Path.GetFileName(normalized);
        foreach (var imageDirectory in new[] { "achievement_images", "images", "img" })
        {
            candidate = Path.Combine(settingsDirectory, imageDirectory, fileName);
            if (File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    private sealed record AchievementDefinition(
        string ApiName,
        string DisplayName,
        string Description,
        bool Hidden,
        string? IconPath,
        string? LockedIconPath,
        long? MaxProgress);

    private sealed record AchievementState(
        bool Earned,
        DateTimeOffset? UnlockedAtUtc,
        long? Progress,
        long? MaxProgress);
}
