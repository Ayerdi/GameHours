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

        var ancestorAttempt = TryResolveFromAncestors(executable);
        var resolved = ancestorAttempt.HadCandidates
            ? ancestorAttempt.AppId
            : TryResolveFromBoundedGameTree(executable);
        if (resolved is not null)
        {
            SuccessfulResolutions.TryAdd(executable, resolved);
        }

        return resolved;
    }

    private static AppIdResolutionAttempt TryResolveFromAncestors(string executablePath)
    {
        var candidates = new List<AppIdCandidate>();
        var depth = 0;
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            // Emulator-specific real AppIDs are stronger evidence than a generic steam_appid.txt.
            // OnlineFix commonly uses FakeAppId=480 (Spacewar) for Steam transport while RealAppId
            // remains the actual title whose achievements/state live under Public Documents.
            if (TryReadOnlineFixIni(Path.Combine(directory, "OnlineFix.ini")) is { } onlineFix)
            {
                candidates.Add(new AppIdCandidate(onlineFix, priority: 0, depth));
            }

            if (TryReadSteamEmuIni(Path.Combine(directory, "steam_emu.ini")) is { } emulator)
            {
                candidates.Add(new AppIdCandidate(emulator, priority: 0, depth));
            }

            if (TryReadAppIdFile(Path.Combine(directory, "steam_appid.txt")) is { } direct)
            {
                candidates.Add(new AppIdCandidate(direct, priority: 1, depth));
            }

            if (TryReadAppIdFile(Path.Combine(directory, "steam_settings", "steam_appid.txt")) is { } settings)
            {
                candidates.Add(new AppIdCandidate(settings, priority: 1, depth));
            }

            depth++;
        }

        return ResolveCandidates(candidates);
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

            var onlineFixPath = Path.Combine(directory, "OnlineFix.ini");
            if (TryReadOnlineFixIni(onlineFixPath) is { } onlineFixAppId)
            {
                candidates.Add(new AppIdCandidate(onlineFixAppId, hasSteamApi ? 0 : 1, depth));
            }

            var emulatorPath = Path.Combine(directory, "steam_emu.ini");
            if (TryReadSteamEmuIni(emulatorPath) is { } emulatorAppId)
            {
                // A steam_emu.ini beside the Steam API DLL is the strongest local evidence: it is
                // the layout used by CODEX/RUNE-style releases and by the Gothic 1 Remake build
                // observed in real-machine validation.
                candidates.Add(new AppIdCandidate(emulatorAppId, hasSteamApi ? 0 : 3, depth));
            }

            var appIdPath = Path.Combine(directory, "steam_appid.txt");
            if (TryReadAppIdFile(appIdPath) is { } appId)
            {
                candidates.Add(new AppIdCandidate(appId, hasSteamApi ? 1 : 2, depth));
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

        return ResolveCandidates(candidates).AppId;
    }

    private static AppIdResolutionAttempt ResolveCandidates(IReadOnlyCollection<AppIdCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return new AppIdResolutionAttempt(HadCandidates: false, AppId: null);
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

        // Conflicting equally-strong markers are ambiguous. Do not fall through to weaker
        // evidence or another scan and risk joining this installation to the wrong global state.
        return new AppIdResolutionAttempt(
            HadCandidates: true,
            AppId: best.Length == 1 ? best[0] : null);
    }

    private static string? ResolveLikelyGameRoot(string executablePath)
    {
        foreach (var directory in EnumerateAncestors(executablePath, MaxAncestorDepth))
        {
            if (Directory.Exists(Path.Combine(directory, "Engine")) ||
                Directory.Exists(Path.Combine(directory, "steam_settings")) ||
                Directory.Exists(Path.Combine(directory, "Steam")) ||
                File.Exists(Path.Combine(directory, "OnlineFix.ini")))
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static string? TryReadOnlineFixIni(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var inMainSection = false;
            foreach (var rawLine in File.ReadLines(path).Take(2048))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                {
                    continue;
                }

                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    inMainSection = line[1..^1].Trim()
                        .Equals("Main", StringComparison.OrdinalIgnoreCase);
                    continue;
                }

                if (!inMainSection)
                {
                    continue;
                }

                var separator = line.IndexOf('=');
                if (separator <= 0 ||
                    !line[..separator].Trim().Equals("RealAppId", StringComparison.OrdinalIgnoreCase))
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
    private sealed record AppIdResolutionAttempt(bool HadCandidates, string? AppId);
}
