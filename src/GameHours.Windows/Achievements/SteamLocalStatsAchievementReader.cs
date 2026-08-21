using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Reads Steam's official on-disk achievement catalogue and per-user state from
/// appcache/stats. No Steam IPC or network call is performed.
/// </summary>
public sealed class SteamLocalStatsAchievementReader
{
    public LocalAchievementSnapshot? TryRead(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            var installation = SteamLocalInstallation.TryResolve(executablePath);
            if (installation is null)
            {
                return null;
            }

            var statsDirectory = Path.Combine(installation.SteamRoot, "appcache", "stats");
            var schemaPath = Path.Combine(
                statsDirectory,
                $"UserGameStatsSchema_{installation.AppId}.bin");
            if (!File.Exists(schemaPath))
            {
                return null;
            }

            var userStatsPath = ResolveUserStatsPath(statsDirectory, installation.AppId);
            return TryReadFiles(schemaPath, userStatsPath, installation.AppId);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }

    public LocalAchievementSnapshot? TryReadFiles(
        string schemaPath,
        string? userStatsPath,
        string appId)
    {
        if (string.IsNullOrWhiteSpace(schemaPath) ||
            string.IsNullOrWhiteSpace(appId) ||
            !appId.All(char.IsDigit) ||
            !File.Exists(schemaPath))
        {
            return null;
        }

        try
        {
            var schema = BinaryKeyValueReader.TryRead(schemaPath);
            if (schema is null)
            {
                return null;
            }

            BinaryKeyValueNode? userStats = null;
            if (!string.IsNullOrWhiteSpace(userStatsPath) && File.Exists(userStatsPath))
            {
                userStats = BinaryKeyValueReader.TryRead(userStatsPath);
            }

            var appNode = schema.Child(appId) ?? schema.FindFirst(appId);
            var statsNode = appNode?.Child("stats");
            if (statsNode is null)
            {
                return null;
            }

            var cache = userStats?.FindFirst("cache");
            var achievements = new List<LocalAchievement>();
            foreach (var group in statsNode.Children)
            {
                var bits = group.Child("bits");
                if (bits is null)
                {
                    continue;
                }

                var userGroup = cache?.Child(group.Name);
                var mask = unchecked((uint)(userGroup?.Child("data")?.AsInt32(0) ?? 0));
                var times = userGroup?.Child("AchievementTimes");

                foreach (var bit in bits.Children)
                {
                    if (!uint.TryParse(
                            bit.Name,
                            NumberStyles.Integer,
                            CultureInfo.InvariantCulture,
                            out var position))
                    {
                        continue;
                    }

                    var apiName = bit.Child("name")?.AsString();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    var display = bit.Child("display");
                    var displayName = ReadLocalized(display?.Child("name"), apiName);
                    var description = ReadLocalized(display?.Child("desc"), string.Empty);
                    var hidden = display?.Child("hidden")?.AsBoolean(false) ?? false;

                    var unlocked = position < 32 && ((mask >> (int)position) & 1u) == 1u;
                    var unlockedAt = unlocked
                        ? ReadUnlockTime(times?.Child(position.ToString(CultureInfo.InvariantCulture)))
                        : null;

                    achievements.Add(new LocalAchievement(
                        apiName,
                        displayName,
                        description,
                        hidden,
                        unlocked,
                        unlockedAt,
                        IconPath: null,
                        LockedIconPath: null,
                        Progress: null,
                        MaxProgress: null));
                }
            }

            var normalized = achievements
                .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(item => item.IsUnlocked)
                    .ThenBy(item => item.UnlockedAtUtc ?? DateTimeOffset.MaxValue)
                    .First())
                .ToArray();
            if (normalized.Length == 0)
            {
                return null;
            }

            return new LocalAchievementSnapshot(
                "Steam local stats",
                appId,
                Path.GetFullPath(schemaPath),
                !string.IsNullOrWhiteSpace(userStatsPath) && File.Exists(userStatsPath)
                    ? Path.GetFullPath(userStatsPath)
                    : null,
                normalized)
            {
                IsCatalogueComplete = true
            };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or DecoderFallbackException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ResolveUserStatsPath(string statsDirectory, string appId)
    {
        if (!Directory.Exists(statsDirectory))
        {
            return null;
        }

        var activeAccountId = TryReadActiveSteamAccountId();
        if (activeAccountId is not null)
        {
            var activePath = Path.Combine(
                statsDirectory,
                $"UserGameStats_{activeAccountId}_{appId}.bin");
            if (File.Exists(activePath))
            {
                return activePath;
            }
        }

        string[] candidates;
        try
        {
            candidates = Directory.GetFiles(
                statsDirectory,
                $"UserGameStats_*_{appId}.bin");
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        // Never merge or guess between multiple Steam accounts. If exactly one state file
        // exists, it is unambiguous enough to use when ActiveUser is unavailable.
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private static string? TryReadActiveSteamAccountId()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            var value = key?.GetValue("ActiveUser");
            var parsed = value switch
            {
                int number when number > 0 => ((uint)number).ToString(CultureInfo.InvariantCulture),
                uint number when number > 0 => number.ToString(CultureInfo.InvariantCulture),
                long number when number > 0 && number <= uint.MaxValue => number.ToString(CultureInfo.InvariantCulture),
                string text when uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) && number > 0
                    => number.ToString(CultureInfo.InvariantCulture),
                _ => null
            };
            return parsed;
        }
        catch
        {
            return null;
        }
    }

    private static DateTimeOffset? ReadUnlockTime(BinaryKeyValueNode? node)
    {
        var seconds = node?.AsInt32(0) ?? 0;
        if (seconds <= 0)
        {
            return null;
        }

        try
        {
            return DateTimeOffset.FromUnixTimeSeconds(unchecked((uint)seconds));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string ReadLocalized(BinaryKeyValueNode? node, string fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        var direct = node.AsString();
        if (!string.IsNullOrWhiteSpace(direct))
        {
            return direct;
        }

        foreach (var language in PreferredSteamLanguages())
        {
            var value = node.Child(language)?.AsString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return node.Children
                   .Select(child => child.AsString())
                   .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
               ?? fallback;
    }

    private static IReadOnlyList<string> PreferredSteamLanguages() =>
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName switch
        {
            "es" => new[] { "spanish", "latam", "english" },
            "pt" => new[] { "brazilian", "portuguese", "english" },
            "fr" => new[] { "french", "english" },
            "de" => new[] { "german", "english" },
            "it" => new[] { "italian", "english" },
            _ => new[] { "english" }
        };
}

internal sealed record SteamInstalledApp(string AppId, string SteamRoot);

internal static class SteamLocalInstallation
{
    public static SteamInstalledApp? TryResolve(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        var executable = Path.GetFullPath(executablePath);
        foreach (var steamRoot in FindSteamRoots())
        {
            foreach (var library in FindSteamLibraries(steamRoot))
            {
                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }

                string[] manifests;
                try
                {
                    manifests = Directory.GetFiles(steamApps, "appmanifest_*.acf");
                }
                catch (IOException)
                {
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    try
                    {
                        var text = File.ReadAllText(manifest);
                        var appId = GetVdfValue(text, "appid");
                        var installDirName = GetVdfValue(text, "installdir");
                        if (string.IsNullOrWhiteSpace(appId) ||
                            !appId.All(char.IsDigit) ||
                            string.IsNullOrWhiteSpace(installDirName))
                        {
                            continue;
                        }

                        var installDirectory = Path.GetFullPath(
                            Path.Combine(library, "steamapps", "common", installDirName));
                        if (IsPathWithin(executable, installDirectory))
                        {
                            return new SteamInstalledApp(appId, steamRoot);
                        }
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }

        return null;
    }

    private static IEnumerable<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
            {
                roots.Add(Path.GetFullPath(steamPath));
            }
        }
        catch
        {
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        if (!string.IsNullOrWhiteSpace(programFilesX86))
        {
            var defaultRoot = Path.Combine(programFilesX86, "Steam");
            if (Directory.Exists(defaultRoot))
            {
                roots.Add(Path.GetFullPath(defaultRoot));
            }
        }

        return roots;
    }

    private static IEnumerable<string> FindSteamLibraries(string steamRoot)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(steamRoot)
        };

        foreach (var file in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf")
                 })
        {
            if (!File.Exists(file))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(file);
                foreach (Match match in Regex.Matches(
                             text,
                             "\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"",
                             RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                {
                    var value = UnescapeVdf(match.Groups[1].Value);
                    if (Directory.Exists(value))
                    {
                        libraries.Add(Path.GetFullPath(value));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return libraries;
    }

    private static string? GetVdfValue(string text, string key)
    {
        var pattern = $"\\\"{Regex.Escape(key)}\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"";
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? UnescapeVdf(match.Groups[1].Value) : null;
    }

    private static string UnescapeVdf(string value) =>
        value.Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    private static bool IsPathWithin(string path, string root)
    {
        var fullPath = Path.GetFullPath(path);
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class BinaryKeyValueNode
{
    public BinaryKeyValueNode(string name, object? value = null)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public object? Value { get; }
    public List<BinaryKeyValueNode> Children { get; } = new();

    public BinaryKeyValueNode? Child(string name) =>
        Children.FirstOrDefault(child => child.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public BinaryKeyValueNode? FindFirst(string name)
    {
        if (Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return this;
        }

        foreach (var child in Children)
        {
            var match = child.FindFirst(name);
            if (match is not null)
            {
                return match;
            }
        }

        return null;
    }

    public string? AsString() => Value switch
    {
        string text => text,
        int number => number.ToString(CultureInfo.InvariantCulture),
        uint number => number.ToString(CultureInfo.InvariantCulture),
        ulong number => number.ToString(CultureInfo.InvariantCulture),
        float number => number.ToString(CultureInfo.InvariantCulture),
        _ => null
    };

    public int AsInt32(int fallback) => Value switch
    {
        int number => number,
        uint number => unchecked((int)number),
        ulong number => unchecked((int)number),
        float number => (int)number,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
        _ => fallback
    };

    public bool AsBoolean(bool fallback) => Value switch
    {
        int number => number != 0,
        uint number => number != 0,
        ulong number => number != 0,
        float number => Math.Abs(number) > float.Epsilon,
        string text when bool.TryParse(text, out var boolean) => boolean,
        string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number != 0,
        _ => fallback
    };
}

internal static class BinaryKeyValueReader
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private const int MaxDepth = 64;
    private const int MaxNodes = 500_000;
    private const int MaxStringBytes = 1024 * 1024;

    private enum ValueType : byte
    {
        None = 0,
        String = 1,
        Int32 = 2,
        Float32 = 3,
        Pointer = 4,
        WideString = 5,
        Color = 6,
        UInt64 = 7,
        End = 8
    }

    public static BinaryKeyValueNode? TryRead(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxFileBytes)
            {
                return null;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
            var root = new BinaryKeyValueNode("<root>");
            var parser = new Parser(reader);
            parser.ReadChildren(root, depth: 0);
            return stream.Position == stream.Length ? root : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            EndOfStreamException or FormatException or DecoderFallbackException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed class Parser
    {
        private readonly BinaryReader _reader;
        private int _nodesRead;

        public Parser(BinaryReader reader)
        {
            _reader = reader;
        }

        public void ReadChildren(BinaryKeyValueNode parent, int depth)
        {
            if (depth > MaxDepth)
            {
                throw new FormatException("Binary KeyValues nesting is too deep.");
            }

            while (true)
            {
                EnsureAvailable(1);
                var type = (ValueType)_reader.ReadByte();
                if (type == ValueType.End)
                {
                    return;
                }

                _nodesRead++;
                if (_nodesRead > MaxNodes)
                {
                    throw new FormatException("Binary KeyValues contains too many nodes.");
                }

                var name = ReadNullTerminatedUtf8();
                var node = type switch
                {
                    ValueType.None => ReadObject(name, depth),
                    ValueType.String => new BinaryKeyValueNode(name, ReadNullTerminatedUtf8()),
                    ValueType.Int32 => new BinaryKeyValueNode(name, ReadInt32()),
                    ValueType.Float32 => new BinaryKeyValueNode(name, ReadSingle()),
                    ValueType.Pointer => new BinaryKeyValueNode(name, ReadUInt32()),
                    ValueType.WideString => new BinaryKeyValueNode(name, ReadNullTerminatedUtf16()),
                    ValueType.Color => new BinaryKeyValueNode(name, ReadUInt32()),
                    ValueType.UInt64 => new BinaryKeyValueNode(name, ReadUInt64()),
                    _ => throw new FormatException($"Unsupported Binary KeyValues type {(byte)type}.")
                };
                parent.Children.Add(node);
            }
        }

        private BinaryKeyValueNode ReadObject(string name, int depth)
        {
            var node = new BinaryKeyValueNode(name);
            ReadChildren(node, depth + 1);
            return node;
        }

        private string ReadNullTerminatedUtf8()
        {
            var bytes = new List<byte>();
            while (true)
            {
                EnsureAvailable(1);
                var value = _reader.ReadByte();
                if (value == 0)
                {
                    return Encoding.UTF8.GetString(bytes.ToArray());
                }

                bytes.Add(value);
                if (bytes.Count > MaxStringBytes)
                {
                    throw new FormatException("Binary KeyValues string is too large.");
                }
            }
        }

        private string ReadNullTerminatedUtf16()
        {
            var bytes = new List<byte>();
            while (true)
            {
                EnsureAvailable(2);
                var low = _reader.ReadByte();
                var high = _reader.ReadByte();
                if (low == 0 && high == 0)
                {
                    return Encoding.Unicode.GetString(bytes.ToArray());
                }

                bytes.Add(low);
                bytes.Add(high);
                if (bytes.Count > MaxStringBytes)
                {
                    throw new FormatException("Binary KeyValues wide string is too large.");
                }
            }
        }

        private int ReadInt32()
        {
            EnsureAvailable(sizeof(int));
            return _reader.ReadInt32();
        }

        private uint ReadUInt32()
        {
            EnsureAvailable(sizeof(uint));
            return _reader.ReadUInt32();
        }

        private ulong ReadUInt64()
        {
            EnsureAvailable(sizeof(ulong));
            return _reader.ReadUInt64();
        }

        private float ReadSingle()
        {
            EnsureAvailable(sizeof(float));
            return _reader.ReadSingle();
        }

        private void EnsureAvailable(int bytes)
        {
            if (_reader.BaseStream.Length - _reader.BaseStream.Position < bytes)
            {
                throw new EndOfStreamException();
            }
        }
    }
}
