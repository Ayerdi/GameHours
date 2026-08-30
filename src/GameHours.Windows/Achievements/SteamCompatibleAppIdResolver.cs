using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Resolves a Steam AppID from local installation evidence. Explicit runtime/emulator metadata
/// is preferred over generic override files, equally strong contradictions fail closed, and
/// successful identities are cached in memory plus a fingerprinted machine-local derived cache.
/// No network lookup is performed here.
/// </summary>
public sealed class SteamCompatibleAppIdResolver
{
    private const int MaxAncestorDepth = 6;
    private const int MaxTreeDepth = 8;
    private const int MaxDirectories = 3000;
    private const int MaxConfigLines = 2048;

    private readonly ConcurrentDictionary<string, SteamAppIdResolution> _successfulResolutions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SteamAppIdResolutionCache? _persistentCache;

    public SteamCompatibleAppIdResolver()
        : this(GetDefaultPersistentCachePath())
    {
    }

    /// <summary>
    /// Creates a resolver with an explicit derived-cache path. Pass null to disable persistent
    /// caching, which is useful for isolated tests. Current local evidence always wins over cache.
    /// </summary>
    public SteamCompatibleAppIdResolver(string? persistentCachePath)
    {
        _persistentCache = string.IsNullOrWhiteSpace(persistentCachePath)
            ? null
            : new SteamAppIdResolutionCache(persistentCachePath);
    }

    public string? TryResolve(string executablePath) =>
        TryResolveDetailed(executablePath)?.AppId;

    public SteamAppIdResolution? TryResolveDetailed(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        string executable;
        try
        {
            executable = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (_successfulResolutions.TryGetValue(executable, out var cached))
        {
            return cached;
        }

        var candidates = new List<AppIdCandidate>();
        CollectSteamManifestCandidates(executable, candidates);
        CollectAncestorCandidates(executable, candidates);
        CollectBoundedGameTreeCandidates(executable, candidates);

        var attempt = ResolveCandidates(candidates);
        if (attempt.HadCandidates)
        {
            if (attempt.Resolution is null)
            {
                return null;
            }

            Remember(executable, attempt.Resolution);
            return attempt.Resolution;
        }

        var persisted = _persistentCache?.TryRead(executable);
        if (persisted is not null)
        {
            _successfulResolutions.TryAdd(executable, persisted);
        }

        return persisted;
    }

    private void Remember(string executablePath, SteamAppIdResolution resolution)
    {
        _successfulResolutions[executablePath] = resolution;
        _persistentCache?.TryWrite(executablePath, resolution);
    }

    private static void CollectAncestorCandidates(
        string executablePath,
        ICollection<AppIdCandidate> candidates)
    {
        var depth = 0;
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            AddExplicitConfigCandidates(directory, depth, priority: 0, candidates);

            var steamSettingsAppId = Path.Combine(directory, "steam_settings", "steam_appid.txt");
            if (TryReadAppIdFile(steamSettingsAppId) is { } settings)
            {
                candidates.Add(new AppIdCandidate(
                    settings,
                    0,
                    depth,
                    "Goldberg/GBE steam_settings AppID",
                    steamSettingsAppId,
                    SteamAppIdConfidence.High));
            }

            var genericAppId = Path.Combine(directory, "steam_appid.txt");
            if (TryReadAppIdFile(genericAppId) is { } direct)
            {
                // Valve documents steam_appid.txt as a development/override hint rather than
                // authoritative shipped product identity, so it remains weaker than explicit
                // emulator configuration and Steam appmanifest ownership.
                candidates.Add(new AppIdCandidate(
                    direct,
                    2,
                    depth,
                    "steam_appid.txt",
                    genericAppId,
                    SteamAppIdConfidence.Medium));
            }

            depth++;
        }
    }

    private static void CollectBoundedGameTreeCandidates(
        string executablePath,
        ICollection<AppIdCandidate> candidates)
    {
        var root = ResolveLikelyGameRoot(executablePath);
        if (root is null)
        {
            return;
        }

        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < MaxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            visited++;

            var hasSteamApi = File.Exists(Path.Combine(directory, "steam_api64.dll")) ||
                              File.Exists(Path.Combine(directory, "steam_api.dll"));
            AddExplicitConfigCandidates(
                directory,
                depth,
                priority: hasSteamApi ? 0 : 1,
                candidates);

            var settingsPath = Path.Combine(directory, "steam_settings", "steam_appid.txt");
            if (TryReadAppIdFile(settingsPath) is { } settingsAppId)
            {
                candidates.Add(new AppIdCandidate(
                    settingsAppId,
                    hasSteamApi ? 0 : 1,
                    depth,
                    "Goldberg/GBE steam_settings AppID",
                    settingsPath,
                    SteamAppIdConfidence.High));
            }

            var genericPath = Path.Combine(directory, "steam_appid.txt");
            if (TryReadAppIdFile(genericPath) is { } genericAppId)
            {
                candidates.Add(new AppIdCandidate(
                    genericAppId,
                    hasSteamApi ? 1 : 3,
                    depth,
                    "steam_appid.txt",
                    genericPath,
                    SteamAppIdConfidence.Medium));
            }

            if (depth >= MaxTreeDepth)
            {
                continue;
            }

            foreach (var child in EnumerateChildDirectories(directory)
                         .OrderByDescending(IsLikelySteamRuntimeDirectory)
                         .ThenBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                queue.Enqueue((child, depth + 1));
            }
        }
    }

    private static void AddExplicitConfigCandidates(
        string directory,
        int depth,
        int priority,
        ICollection<AppIdCandidate> candidates)
    {
        AddIniCandidate(
            Path.Combine(directory, "OnlineFix.ini"),
            requiredSection: "Main",
            key: "RealAppId",
            source: "OnlineFix RealAppId",
            depth,
            priority,
            candidates);
        AddIniCandidate(
            Path.Combine(directory, "steam_emu.ini"),
            requiredSection: null,
            key: "AppId",
            source: "steam_emu.ini AppId",
            depth,
            priority,
            candidates);
        AddIniCandidate(
            Path.Combine(directory, "CPY.ini"),
            requiredSection: "Settings",
            key: "AppID",
            source: "CPY AppID",
            depth,
            priority,
            candidates);
        AddIniCandidate(
            Path.Combine(directory, "SmartSteamEmu.ini"),
            requiredSection: "SmartSteamEmu",
            key: "AppId",
            source: "SmartSteamEmu AppId",
            depth,
            priority,
            candidates);
        AddIniCandidate(
            Path.Combine(directory, "tenoke.ini"),
            requiredSection: "TENOKE",
            key: "id",
            source: "TENOKE id",
            depth,
            priority,
            candidates);
        AddIniCandidate(
            Path.Combine(directory, "ColdClientLoader.ini"),
            requiredSection: "SteamClient",
            key: "AppId",
            source: "ColdClientLoader AppId",
            depth,
            priority,
            candidates);
    }

    private static void AddIniCandidate(
        string path,
        string? requiredSection,
        string key,
        string source,
        int depth,
        int priority,
        ICollection<AppIdCandidate> candidates)
    {
        if (TryReadIniValue(path, requiredSection, key) is not { } appId)
        {
            return;
        }

        candidates.Add(new AppIdCandidate(
            appId,
            priority,
            depth,
            source,
            path,
            SteamAppIdConfidence.High));
    }

    private static AppIdResolutionAttempt ResolveCandidates(
        IReadOnlyCollection<AppIdCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new AppIdResolutionAttempt(HadCandidates: false, Resolution: null);
        }

        var bestPriority = candidates.Min(candidate => candidate.Priority);
        var strongest = candidates
            .Where(candidate => candidate.Priority == bestPriority)
            .ToArray();
        var appIds = strongest
            .Select(candidate => candidate.AppId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Two equally strong identities are a real conflict even when one happens to be closer
        // to the executable. Refuse to join global state to either game instead of guessing.
        if (appIds.Length != 1)
        {
            return new AppIdResolutionAttempt(HadCandidates: true, Resolution: null);
        }

        var chosen = strongest
            .Where(candidate => string.Equals(candidate.AppId, appIds[0], StringComparison.Ordinal))
            .OrderBy(candidate => candidate.Depth)
            .ThenBy(candidate => candidate.EvidencePath, StringComparer.OrdinalIgnoreCase)
            .First();
        return new AppIdResolutionAttempt(
            HadCandidates: true,
            new SteamAppIdResolution(
                chosen.AppId,
                chosen.EvidenceSource,
                chosen.EvidencePath,
                chosen.Confidence));
    }

    private static string? ResolveLikelyGameRoot(string executablePath)
    {
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            if (HasLocalIdentityMarker(directory) ||
                Directory.Exists(Path.Combine(directory, "Engine")) ||
                Directory.Exists(Path.Combine(directory, "Steam")) ||
                HasUnityDataDirectory(directory))
            {
                return directory;
            }
        }

        return null;
    }

    private static bool HasLocalIdentityMarker(string directory) =>
        Directory.Exists(Path.Combine(directory, "steam_settings")) ||
        File.Exists(Path.Combine(directory, "steam_appid.txt")) ||
        File.Exists(Path.Combine(directory, "OnlineFix.ini")) ||
        File.Exists(Path.Combine(directory, "steam_emu.ini")) ||
        File.Exists(Path.Combine(directory, "CPY.ini")) ||
        File.Exists(Path.Combine(directory, "SmartSteamEmu.ini")) ||
        File.Exists(Path.Combine(directory, "tenoke.ini")) ||
        File.Exists(Path.Combine(directory, "ColdClientLoader.ini"));

    private static bool HasUnityDataDirectory(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory, "*_Data", SearchOption.TopDirectoryOnly)
                .Take(16)
                .Any();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return false;
        }
    }

    private static void CollectSteamManifestCandidates(
        string executablePath,
        ICollection<AppIdCandidate> candidates)
    {
        var steamCommonMarker = $"{Path.DirectorySeparatorChar}steamapps{Path.DirectorySeparatorChar}common{Path.DirectorySeparatorChar}";
        if (!executablePath.Contains(steamCommonMarker, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

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
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
                {
                    continue;
                }

                foreach (var manifest in manifests)
                {
                    try
                    {
                        var text = File.ReadAllText(manifest);
                        var appId = NormalizeAppId(GetVdfValue(text, "appid"));
                        var installDirName = GetVdfValue(text, "installdir");
                        if (appId is null || string.IsNullOrWhiteSpace(installDirName))
                        {
                            continue;
                        }

                        var installDirectory = Path.GetFullPath(
                            Path.Combine(library, "steamapps", "common", installDirName));
                        if (!IsPathWithin(executablePath, installDirectory))
                        {
                            continue;
                        }

                        candidates.Add(new AppIdCandidate(
                            appId,
                            0,
                            0,
                            "Steam appmanifest",
                            manifest,
                            SteamAppIdConfidence.High));
                    }
                    catch (Exception exception) when (
                        exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
                    {
                    }
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string executablePath, int maxDepth)
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

    private static IEnumerable<string> EnumerateChildDirectories(string directory)
    {
        string[] children;
        try
        {
            children = Directory.GetDirectories(directory);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            yield break;
        }

        foreach (var child in children)
        {
            try
            {
                if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
            {
                continue;
            }

            yield return child;
        }
    }

    private static bool IsLikelySteamRuntimeDirectory(string path)
    {
        var name = Path.GetFileName(path);
        return name.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Engine", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Binaries", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("ThirdParty", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Plugins", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("x86", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryReadAppIdFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return NormalizeAppId(File.ReadAllText(path));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? TryReadIniValue(
        string path,
        string? requiredSection,
        string requiredKey)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            string? section = null;
            foreach (var rawLine in File.ReadLines(path).Take(MaxConfigLines))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
                {
                    section = line[1..^1].Trim();
                    continue;
                }

                if (requiredSection is not null &&
                    !string.Equals(section, requiredSection, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !line[..separator].Trim().Equals(requiredKey, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rawValue = line[(separator + 1)..];
                var comment = rawValue.IndexOfAny(new[] { '#', ';' });
                if (comment >= 0)
                {
                    rawValue = rawValue[..comment];
                }

                return NormalizeAppId(rawValue.Trim().Trim('"', '\''));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
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
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
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

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private static string? GetDefaultPersistentCachePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return string.IsNullOrWhiteSpace(localAppData)
            ? null
            : Path.Combine(localAppData, "GameHours", "cache", "steam-appid-identities.json");
    }

    private sealed record AppIdCandidate(
        string AppId,
        int Priority,
        int Depth,
        string EvidenceSource,
        string EvidencePath,
        SteamAppIdConfidence Confidence);

    private sealed record AppIdResolutionAttempt(
        bool HadCandidates,
        SteamAppIdResolution? Resolution);
}
