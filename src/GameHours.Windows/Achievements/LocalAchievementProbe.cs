using Microsoft.Win32;

namespace GameHours.Windows.Achievements;

public sealed record LocalAchievementFinding(
    string Kind,
    string Path,
    string Detail);

public sealed record LocalAchievementProbeResult(
    string? GameRoot,
    string? SteamAppId,
    IReadOnlyList<LocalAchievementFinding> Findings);

public sealed class LocalAchievementProbe
{
    private static readonly string[] InterestingFileNames =
    {
        "achievements.json",
        "achievement.json",
        "user_achievements.json",
        "stats.json",
        "steam_appid.txt"
    };

    public LocalAchievementProbeResult Probe(
        string gameTitle,
        string executablePath,
        string? knownInstallDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(gameTitle);
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var executable = Path.GetFullPath(executablePath);
        var gameRoot = ResolveGameRoot(executable, knownInstallDirectory);
        var findings = new List<LocalAchievementFinding>();

        string? appId = null;
        if (!string.IsNullOrWhiteSpace(gameRoot) && Directory.Exists(gameRoot))
        {
            ProbeInstallTree(gameRoot, findings, ref appId);
        }

        if (!string.IsNullOrWhiteSpace(appId))
        {
            ProbeSteamCaches(appId, findings);
            ProbeEmulatorCaches(appId, findings);
        }

        ProbeLikelySaveDirectories(gameTitle, executable, findings);

        var unique = findings
            .GroupBy(item => $"{item.Kind}\n{item.Path}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Kind, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LocalAchievementProbeResult(gameRoot, appId, unique);
    }

    private static string ResolveGameRoot(string executablePath, string? knownInstallDirectory)
    {
        if (!string.IsNullOrWhiteSpace(knownInstallDirectory) && Directory.Exists(knownInstallDirectory))
        {
            return Path.GetFullPath(knownInstallDirectory);
        }

        var current = Directory.GetParent(Path.GetDirectoryName(executablePath)!)?.FullName
            ?? Path.GetDirectoryName(executablePath)!;
        var fallback = Path.GetDirectoryName(executablePath)!;

        for (var depth = 0; depth < 6 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            if (File.Exists(Path.Combine(current, "steam_appid.txt")) ||
                Directory.Exists(Path.Combine(current, "steam_settings")) ||
                Directory.Exists(Path.Combine(current, "Engine")))
            {
                return current;
            }

            fallback = current;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, Path.GetPathRoot(parent), StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        // Keep the fallback conservative: never scan an arbitrary drive root.
        return Directory.GetParent(Path.GetDirectoryName(executablePath)!)?.FullName
            ?? Path.GetDirectoryName(executablePath)!;
    }

    private static void ProbeInstallTree(
        string gameRoot,
        List<LocalAchievementFinding> findings,
        ref string? appId)
    {
        foreach (var file in EnumerateFilesLimited(gameRoot, maxDepth: 5, maxFiles: 12000))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("steam_appid.txt", StringComparison.OrdinalIgnoreCase))
            {
                var parsed = TryReadAppId(file);
                if (!string.IsNullOrWhiteSpace(parsed))
                {
                    appId ??= parsed;
                    findings.Add(new LocalAchievementFinding(
                        "steam_appid",
                        file,
                        $"Steam AppID hint: {parsed}"));
                }
                else
                {
                    findings.Add(new LocalAchievementFinding(
                        "steam_appid",
                        file,
                        "Steam AppID marker found, but its value could not be parsed."));
                }

                continue;
            }

            if (InterestingFileNames.Any(name => fileName.Equals(name, StringComparison.OrdinalIgnoreCase)) ||
                fileName.Contains("achiev", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new LocalAchievementFinding(
                    "game_file",
                    file,
                    DescribeGameFile(file)));
            }
        }

        foreach (var directory in EnumerateDirectoriesLimited(gameRoot, maxDepth: 4, maxDirectories: 2500))
        {
            var name = Path.GetFileName(directory);
            if (name.Equals("steam_settings", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("achievement", StringComparison.OrdinalIgnoreCase))
            {
                findings.Add(new LocalAchievementFinding(
                    "game_directory",
                    directory,
                    name.Equals("steam_settings", StringComparison.OrdinalIgnoreCase)
                        ? "Steam-compatible local settings directory; it may contain achievement definitions."
                        : "Directory name suggests achievement data."));
            }
        }
    }

    private static void ProbeSteamCaches(string appId, List<LocalAchievementFinding> findings)
    {
        foreach (var steamRoot in FindSteamRoots())
        {
            var statsRoot = Path.Combine(steamRoot, "appcache", "stats");
            if (!Directory.Exists(statsRoot))
            {
                continue;
            }

            var schema = Path.Combine(statsRoot, $"UserGameStatsSchema_{appId}.bin");
            if (File.Exists(schema))
            {
                findings.Add(new LocalAchievementFinding(
                    "steam_schema",
                    schema,
                    "Steam local achievement/stat schema."));
            }

            try
            {
                foreach (var userStats in Directory.EnumerateFiles(statsRoot, $"UserGameStats_*_{appId}.bin"))
                {
                    findings.Add(new LocalAchievementFinding(
                        "steam_user_stats",
                        userStats,
                        "Steam local per-user achievement/stat state."));
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void ProbeEmulatorCaches(string appId, List<LocalAchievementFinding> findings)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roaming))
        {
            return;
        }

        var candidates = new[]
        {
            Path.Combine(roaming, "Goldberg SteamEmu Saves", appId),
            Path.Combine(roaming, "GSE Saves", appId),
            Path.Combine(roaming, "Goldberg SteamEmu Saves", "settings", appId),
            Path.Combine(roaming, "SmartSteamEmu", appId)
        };

        foreach (var directory in candidates.Where(Directory.Exists))
        {
            findings.Add(new LocalAchievementFinding(
                "local_steam_compatible_save",
                directory,
                "Steam-compatible local save directory for this AppID."));

            foreach (var file in EnumerateFilesLimited(directory, maxDepth: 4, maxFiles: 1000))
            {
                var name = Path.GetFileName(file);
                if (InterestingFileNames.Any(expected => name.Equals(expected, StringComparison.OrdinalIgnoreCase)) ||
                    name.Contains("achiev", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("stat", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new LocalAchievementFinding(
                        "local_steam_compatible_file",
                        file,
                        DescribeGameFile(file)));
                }
            }
        }
    }

    private static void ProbeLikelySaveDirectories(
        string gameTitle,
        string executablePath,
        List<LocalAchievementFinding> findings)
    {
        var tokens = BuildStrongTokens(gameTitle, Path.GetFileNameWithoutExtension(executablePath));
        if (tokens.Count == 0)
        {
            return;
        }

        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) is { Length: > 0 } profile
                ? Path.Combine(profile, "Saved Games")
                : string.Empty
        }
        .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            IEnumerable<string> firstLevel;
            try
            {
                firstLevel = Directory.EnumerateDirectories(root).Take(400).ToArray();
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var directory in firstLevel)
            {
                var normalizedName = Normalize(Path.GetFileName(directory));
                if (!tokens.Any(token => normalizedName.Contains(token, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                findings.Add(new LocalAchievementFinding(
                    "possible_save_directory",
                    directory,
                    "Top-level user-data directory matches the game title/executable."));

                foreach (var file in EnumerateFilesLimited(directory, maxDepth: 5, maxFiles: 3000))
                {
                    var name = Path.GetFileName(file);
                    if (name.Contains("achiev", StringComparison.OrdinalIgnoreCase) ||
                        name.Equals("stats.json", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("progress", StringComparison.OrdinalIgnoreCase))
                    {
                        findings.Add(new LocalAchievementFinding(
                            "possible_save_file",
                            file,
                            DescribeGameFile(file)));
                    }
                }
            }
        }
    }

    private static IReadOnlyList<string> BuildStrongTokens(string title, string executableName)
    {
        return new[] { title, executableName }
            .Select(Normalize)
            .Where(token => token.Length >= 5)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string Normalize(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static string? TryReadAppId(string path)
    {
        try
        {
            var text = File.ReadAllText(path).Trim();
            return text.Length > 0 && text.All(char.IsDigit) ? text : null;
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

    private static string DescribeGameFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"Candidate achievement/stat file ({info.Length:N0} bytes).";
        }
        catch
        {
            return "Candidate achievement/stat file.";
        }
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

    private static IEnumerable<string> EnumerateFilesLimited(string root, int maxDepth, int maxFiles)
    {
        var yielded = 0;
        foreach (var entry in EnumerateTreeLimited(root, maxDepth, includeFiles: true, maxEntries: maxFiles))
        {
            if (File.Exists(entry))
            {
                yield return entry;
                yielded++;
                if (yielded >= maxFiles)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateDirectoriesLimited(string root, int maxDepth, int maxDirectories)
    {
        var yielded = 0;
        foreach (var entry in EnumerateTreeLimited(root, maxDepth, includeFiles: false, maxEntries: maxDirectories))
        {
            if (Directory.Exists(entry))
            {
                yield return entry;
                yielded++;
                if (yielded >= maxDirectories)
                {
                    yield break;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateTreeLimited(
        string root,
        int maxDepth,
        bool includeFiles,
        int maxEntries)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var visited = 0;

        while (queue.Count > 0 && visited < maxEntries)
        {
            var (directory, depth) = queue.Dequeue();
            visited++;

            string[] subdirectories;
            try
            {
                subdirectories = Directory.GetDirectories(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var subdirectory in subdirectories)
            {
                yield return subdirectory;
                if (depth < maxDepth)
                {
                    queue.Enqueue((subdirectory, depth + 1));
                }
            }

            if (!includeFiles)
            {
                continue;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(directory);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return file;
            }
        }
    }
}
