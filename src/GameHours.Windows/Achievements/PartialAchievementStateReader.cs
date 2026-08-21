using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Parses local state files that normally contain unlock state but not a complete achievement catalogue.
/// Returned snapshots are intentionally labelled as partial state.
/// </summary>
public sealed class PartialAchievementStateReader
{
    public bool Supports(LocalAchievementSourceKind kind) => kind is
        LocalAchievementSourceKind.Codex or
        LocalAchievementSourceKind.Rune or
        LocalAchievementSourceKind.OnlineFix or
        LocalAchievementSourceKind.Empress or
        LocalAchievementSourceKind.Rld or
        LocalAchievementSourceKind.Skidrow or
        LocalAchievementSourceKind.CreamApi or
        LocalAchievementSourceKind.Rle or
        LocalAchievementSourceKind.Razor1911 or
        LocalAchievementSourceKind.UserStats or
        LocalAchievementSourceKind.ThreeDm or
        LocalAchievementSourceKind.Ali213;

    public LocalAchievementSnapshot? TryRead(LocalAchievementSourceCandidate candidate)
    {
        if (!Supports(candidate.Kind) || !File.Exists(candidate.FilePath))
        {
            return null;
        }

        try
        {
            var unlocked = candidate.Kind switch
            {
                LocalAchievementSourceKind.Codex => ParseDefaultIni(candidate.FilePath),
                LocalAchievementSourceKind.Rune => ParseDefaultIni(candidate.FilePath),
                LocalAchievementSourceKind.Rle => ParseDefaultIni(candidate.FilePath),
                LocalAchievementSourceKind.OnlineFix => ParseOnlineFix(candidate.FilePath),
                LocalAchievementSourceKind.Empress => ParseGoldbergLikeJson(candidate.FilePath),
                LocalAchievementSourceKind.Rld => ParseRld(candidate.FilePath),
                LocalAchievementSourceKind.Skidrow => ParseSkidrow(candidate.FilePath),
                LocalAchievementSourceKind.CreamApi => ParseCreamApi(candidate.FilePath),
                LocalAchievementSourceKind.Razor1911 => ParseRazor1911(candidate.FilePath),
                LocalAchievementSourceKind.UserStats => ParseUserStats(candidate.FilePath),
                LocalAchievementSourceKind.ThreeDm => ParseThreeDm(candidate.FilePath),
                LocalAchievementSourceKind.Ali213 => ParseAli213(candidate.FilePath),
                _ => Array.Empty<UnlockedState>()
            };

            var achievements = unlocked
                .Where(item => !string.IsNullOrWhiteSpace(item.ApiName))
                .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(item => item.UnlockedAtUtc).First())
                .Select(item => new LocalAchievement(
                    item.ApiName,
                    item.ApiName,
                    string.Empty,
                    Hidden: false,
                    IsUnlocked: true,
                    UnlockedAtUtc: item.UnlockedAtUtc,
                    IconPath: null,
                    LockedIconPath: null,
                    Progress: null,
                    MaxProgress: null))
                .ToArray();

            return new LocalAchievementSnapshot(
                $"{FormatSourceName(candidate.Kind)} local · estado parcial",
                candidate.AppId,
                candidate.FilePath,
                candidate.FilePath,
                achievements);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or JsonException or PathTooLongException)
        {
            return null;
        }
    }

    private static IReadOnlyList<UnlockedState> ParseDefaultIni(string path)
    {
        var ini = ReadIni(path);
        var results = new List<UnlockedState>();
        foreach (var (section, values) in ini)
        {
            if (!ReadTruthy(values, "Achieved"))
            {
                continue;
            }

            results.Add(new UnlockedState(section, ParseTimestamp(Get(values, "UnlockTime"))));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseOnlineFix(string path)
    {
        var ini = ReadIni(path);
        var results = new List<UnlockedState>();
        foreach (var (section, values) in ini)
        {
            if (ReadTruthy(values, "achieved"))
            {
                results.Add(new UnlockedState(section, ParseTimestamp(Get(values, "timestamp"))));
            }
            else if (ReadTruthy(values, "Achieved"))
            {
                results.Add(new UnlockedState(section, ParseTimestamp(Get(values, "TimeUnlocked"))));
            }
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseCreamApi(string path)
    {
        var ini = ReadIni(path);
        var results = new List<UnlockedState>();
        foreach (var (section, values) in ini)
        {
            if (ReadTruthy(values, "achieved"))
            {
                results.Add(new UnlockedState(section, ParseTimestamp(Get(values, "unlocktime"))));
            }
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseSkidrow(string path)
    {
        var ini = ReadIni(path);
        if (!ini.TryGetValue("Achievements", out var achievements))
        {
            return Array.Empty<UnlockedState>();
        }

        var results = new List<UnlockedState>();
        foreach (var (name, raw) in achievements)
        {
            var parts = raw.Split('@', StringSplitOptions.TrimEntries);
            if (parts.Length == 0 || parts[0] != "1")
            {
                continue;
            }

            results.Add(new UnlockedState(name, ParseTimestamp(parts[^1])));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseUserStats(string path)
    {
        var ini = ReadIni(path);
        if (!ini.TryGetValue("ACHIEVEMENTS", out var achievements))
        {
            return Array.Empty<UnlockedState>();
        }

        var results = new List<UnlockedState>();
        foreach (var (name, raw) in achievements)
        {
            if (!Regex.IsMatch(raw, @"\bunlocked\s*=\s*true\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            {
                continue;
            }

            var match = Regex.Match(raw, @"(?:^|[\{,\s])time\s*=\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            results.Add(new UnlockedState(
                name.Trim('"'),
                match.Success ? ParseTimestamp(match.Groups[1].Value) : null));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseThreeDm(string path)
    {
        var ini = ReadIni(path);
        if (!ini.TryGetValue("State", out var states) ||
            !ini.TryGetValue("Time", out var times))
        {
            return Array.Empty<UnlockedState>();
        }

        var results = new List<UnlockedState>();
        foreach (var (name, state) in states)
        {
            if (!string.Equals(state.Trim(), "0101", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            times.TryGetValue(name, out var rawTime);
            results.Add(new UnlockedState(name, ParseLittleEndianHexTimestamp(rawTime)));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseRld(string path)
    {
        var ini = ReadIni(path);
        var results = new List<UnlockedState>();
        foreach (var (section, values) in ini)
        {
            if (section.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (ReadLittleEndianUInt32(Get(values, "State")) != 1)
            {
                continue;
            }

            results.Add(new UnlockedState(
                section,
                ParseLittleEndianHexTimestamp(Get(values, "Time"))));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseAli213(string path)
    {
        var ini = ReadIni(path);
        var results = new List<UnlockedState>();
        foreach (var (section, values) in ini)
        {
            if (!ReadTruthy(values, "HaveAchieved"))
            {
                continue;
            }

            results.Add(new UnlockedState(
                section,
                ParseTimestamp(Get(values, "HaveAchievedTime"))));
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseGoldbergLikeJson(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var results = new List<UnlockedState>();

        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !ReadJsonBool(item, "earned"))
                {
                    continue;
                }

                var name = ReadJsonString(item, "name");
                if (!string.IsNullOrWhiteSpace(name))
                {
                    results.Add(new UnlockedState(name, ReadJsonTimestamp(item, "earned_time")));
                }
            }
        }
        else if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object && ReadJsonBool(property.Value, "earned"))
                {
                    results.Add(new UnlockedState(
                        property.Name,
                        ReadJsonTimestamp(property.Value, "earned_time")));
                }
            }
        }

        return results;
    }

    private static IReadOnlyList<UnlockedState> ParseRazor1911(string path)
    {
        var results = new List<UnlockedState>();
        foreach (var line in ReadLines(path))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2 || parts[1] != "1")
            {
                continue;
            }

            results.Add(new UnlockedState(
                parts[0],
                parts.Length > 2 ? ParseTimestamp(parts[2]) : null));
        }

        return results;
    }

    private static Dictionary<string, Dictionary<string, string>> ReadIni(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var rawLine in ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                section = line[1..^1].Trim();
                if (!result.ContainsKey(section))
                {
                    result[section] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim();
            result[section][key] = value;
        }

        return result;
    }

    private static IEnumerable<string> ReadLines(string path)
    {
        var text = File.ReadAllText(path);
        if (text.Length > 0 && text[0] == '\ufeff')
        {
            text = text[1..];
        }

        return text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
    }

    private static bool ReadTruthy(IReadOnlyDictionary<string, string> values, string key)
    {
        if (!values.TryGetValue(key, out var raw))
        {
            return false;
        }

        var value = raw.Trim().Trim('"');
        return value.Equals("1", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("yes", StringComparison.OrdinalIgnoreCase);
    }

    private static string? Get(IReadOnlyDictionary<string, string> values, string key) =>
        values.TryGetValue(key, out var value) ? value : null;

    private static DateTimeOffset? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().Trim('"');
        if (!long.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            return null;
        }

        try
        {
            if (normalized.Length >= 13)
            {
                return DateTimeOffset.FromUnixTimeMilliseconds(value);
            }

            // Some Steam-emulator formats store a seven-digit value whose unit is 1000 seconds.
            if (normalized.Length == 7)
            {
                return DateTimeOffset.FromUnixTimeSeconds(checked(value * 1000));
            }

            return DateTimeOffset.FromUnixTimeSeconds(value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
        catch (OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset? ParseLittleEndianHexTimestamp(string? raw)
    {
        var value = ReadLittleEndianUInt32(raw);
        if (value is null || value == 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(value.Value);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static uint? ReadLittleEndianUInt32(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var normalized = raw.Trim().Trim('"');
        if (normalized.Length < 8)
        {
            return null;
        }

        try
        {
            var bytes = Convert.FromHexString(normalized[..8]);
            return bytes.Length >= 4 ? BinaryPrimitives.ReadUInt32LittleEndian(bytes) : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool ReadJsonBool(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number when value.TryGetInt64(out var number) => number != 0,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            JsonValueKind.String when long.TryParse(value.GetString(), out var number) => number != 0,
            _ => false
        };
    }

    private static string? ReadJsonString(JsonElement element, string name) =>
        TryGetJsonProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static DateTimeOffset? ReadJsonTimestamp(JsonElement element, string name)
    {
        if (!TryGetJsonProperty(element, name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.Number when value.TryGetInt64(out var number) => ParseTimestamp(number.ToString(CultureInfo.InvariantCulture)),
            JsonValueKind.String => ParseTimestamp(value.GetString()),
            _ => null
        };
    }

    private static bool TryGetJsonProperty(JsonElement element, string name, out JsonElement value)
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

    private static string FormatSourceName(LocalAchievementSourceKind kind) => kind switch
    {
        LocalAchievementSourceKind.Codex => "CODEX",
        LocalAchievementSourceKind.Rune => "RUNE",
        LocalAchievementSourceKind.OnlineFix => "OnlineFix",
        LocalAchievementSourceKind.Empress => "EMPRESS",
        LocalAchievementSourceKind.Rld => "RLD",
        LocalAchievementSourceKind.Skidrow => "SKIDROW",
        LocalAchievementSourceKind.CreamApi => "CreamAPI",
        LocalAchievementSourceKind.Rle => "RLE",
        LocalAchievementSourceKind.Razor1911 => "Razor1911",
        LocalAchievementSourceKind.UserStats => "user_stats.ini",
        LocalAchievementSourceKind.ThreeDm => "3DM",
        LocalAchievementSourceKind.Ali213 => "ALI213",
        _ => kind.ToString()
    };

    private sealed record UnlockedState(string ApiName, DateTimeOffset? UnlockedAtUtc);
}
