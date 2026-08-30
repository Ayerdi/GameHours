using System.Collections.Concurrent;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Resolves a Steam AppID from local Steam-compatible installation markers without scanning
/// outside the game's own installation tree. Successful resolutions are cached because an AppID
/// is installation metadata and does not change during normal play.
/// </summary>
public sealed class SteamCompatibleAppIdResolver
{
    private const int MaxAncestorDepth = 6;
    private const int MaxTreeDepth = 8;
    private const int MaxDirectories = 3000;

    private static readonly ConcurrentDictionary<string, string> SuccessfulResolutions =
        new(StringComparer.OrdinalIgnoreCase);

    public string? TryResolve(string executablePath)
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
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (SuccessfulResolutions.TryGetValue(executable, out var cached))
        {
            return cached;
        }

        var resolved = TryResolveFromAncestors(executable) ?? TryResolveFromBoundedGameTree(executable);
        if (resolved is not null)
        {
            SuccessfulResolutions.TryAdd(executable, resolved);
        }

        return resolved;
    }

    private static string? TryResolveFromAncestors(string executablePath)
    {
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            if (TryReadAppIdFile(Path.Combine(directory, "steam_appid.txt")) is { } direct)
            {
                return direct;
            }

            if (TryReadAppIdFile(Path.Combine(directory, "steam_settings", "steam_appid.txt")) is { } settings)
            {
                return settings;
            }

            if (TryReadSteamEmuIni(Path.Combine(directory, "steam_emu.ini")) is { } emulator)
            {
                return emulator;
            }
        }

        return null;
    }

    private static string? TryResolveFromBoundedGameTree(string executablePath)
    {
        var root = ResolveLikelyGameRoot(executablePath);
        if (root is null)
        {
            return null;
        }

        var candidates = new List<AppIdCandidate>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < MaxDirectories)
        {
            var (directory, depth) = queue.Dequeue();
            visited++;

            var hasSteamApi = File.Exists(Path.Combine(directory, "steam_api64.dll")) ||
                              File.Exists(Path.Combine(directory, "steam_api.dll"));

            var appIdPath = Path.Combine(directory, "steam_appid.txt");
            if (TryReadAppIdFile(appIdPath) is { } appId)
            {
                candidates.Add(new AppIdCandidate(appId, hasSteamApi ? 0 : 2, depth));
            }

            var emulatorPath = Path.Combine(directory, "steam_emu.ini");
            if (TryReadSteamEmuIni(emulatorPath) is { } emulatorAppId)
            {
                // A steam_emu.ini beside the Steam API DLL is the strongest local evidence: it is
                // the layout used by CODEX/RUNE-style releases and by the Gothic 1 Remake build
                // observed in real-machine validation.
                candidates.Add(new AppIdCandidate(emulatorAppId, hasSteamApi ? 0 : 3, depth));
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

        if (candidates.Count == 0)
        {
            return null;
        }

        var bestPriority = candidates.Min(candidate => candidate.Priority);
        var bestDepth = candidates
            .Where(candidate => candidate.Priority == bestPriority)
            .Min(candidate => candidate.Depth);
        var best = candidates
            .Where(candidate => candidate.Priority == bestPriority && candidate.Depth == bestDepth)
            .Select(candidate => candidate.AppId)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        // Conflicting equally-strong markers are ambiguous. Do not guess and risk joining one
        // installation with another game's global emulator state.
        return best.Length == 1 ? best[0] : null;
    }

    private static string? ResolveLikelyGameRoot(string executablePath)
    {
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            if (Directory.Exists(Path.Combine(directory, "Engine")) ||
                Directory.Exists(Path.Combine(directory, "steam_settings")) ||
                Directory.Exists(Path.Combine(directory, "Steam")))
            {
                return directory;
            }
        }

        return null;
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or PathTooLongException)
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
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
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
               name.Equals("Win64", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("Win32", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("x64", StringComparison.OrdinalIgnoreCase) ||
               name.Equals("x86", StringComparison.OrdinalIgnoreCase);
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? TryReadSteamEmuIni(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            foreach (var rawLine in File.ReadLines(path).Take(2048))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !line[..separator].Trim().Equals("AppId", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return NormalizeAppId(line[(separator + 1)..].Trim().Trim('"', '\''));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
        }

        return null;
    }

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private sealed record AppIdCandidate(string AppId, int Priority, int Depth);
}
