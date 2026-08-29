namespace GameHours.Windows.Achievements;

/// <summary>
/// Locates Steam-emulator settings without broadening into sibling game installs.
/// Fast paths cover common flat, Unreal and Unity layouts; a bounded breadth-first
/// fallback stays inside the resolved game root and never follows reparse points.
/// </summary>
internal static class SteamSettingsDirectoryLocator
{
    private const int MaxUpwardLevels = 3;
    private const int MaxSearchDepth = 8;
    private const int MaxDirectoriesVisited = 2000;

    private static readonly HashSet<string> NestedExecutableDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "win32", "win64", "wingdk",
        "x64", "x86", "game", "runtime"
    };

    private static readonly HashSet<string> RootMarkerDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "engine", "binaries", "content", "plugins", "steam_settings"
    };

    private static readonly HashSet<string> RootMarkerFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam_api.dll", "steam_api64.dll", "steam_appid.txt"
    };

    private static readonly HashSet<string> IgnoredSearchDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "audio", "config", "configs", "content", "data", "intermediate",
        "localization", "logs", "movies", "music", "pak", "paks", "saved", "saves",
        "shaders", "sound", "sounds", "textures", "videos"
    };

    private static readonly string[] WindowsBinaryDirectories = { "Win64", "Win32", "WinGDK" };
    private static readonly string[] UnityPluginDirectories = { "x86_64", "x86" };

    internal static string? FindNearest(string executablePath) =>
        FindAll(executablePath).FirstOrDefault();

    internal static IReadOnlyList<string> FindAll(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return Array.Empty<string>();
        }

        var executable = Path.GetFullPath(executablePath);
        var executableDirectory = Path.GetDirectoryName(executable);
        if (string.IsNullOrWhiteSpace(executableDirectory) || !Directory.Exists(executableDirectory))
        {
            return Array.Empty<string>();
        }

        var searchRoot = ResolveGameSearchRoot(executable);
        var found = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in CollectFastPathDirectories(executableDirectory, searchRoot))
        {
            AddSettingsCandidate(searchRoot, Path.Combine(directory, "steam_settings"), found, seen);
            AddSettingsCandidate(searchRoot, Path.Combine(directory, "coldclient", "steam_settings"), found, seen);
        }

        if (found.Count > 0)
        {
            return found;
        }

        SearchBreadthFirst(searchRoot, found, seen);
        return found;
    }

    internal static string ResolveGameSearchRoot(string executablePath)
    {
        var searchRoot = Path.GetDirectoryName(Path.GetFullPath(executablePath))
            ?? throw new ArgumentException("Executable path has no directory.", nameof(executablePath));

        for (var level = 0; level < MaxUpwardLevels; level++)
        {
            var parent = Directory.GetParent(searchRoot)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) || IsFileSystemRoot(parent))
            {
                break;
            }

            var currentIsNestedExecutableDirectory =
                NestedExecutableDirectories.Contains(Path.GetFileName(searchRoot));
            if (!currentIsNestedExecutableDirectory && !HasGameRootMarker(parent))
            {
                break;
            }

            searchRoot = parent;
        }

        return Path.GetFullPath(searchRoot);
    }

    internal static bool IsWithin(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> CollectFastPathDirectories(
        string executableDirectory,
        string searchRoot)
    {
        var directories = new List<string> { executableDirectory, searchRoot };

        foreach (var child in EnumerateDirectoriesSafe(searchRoot))
        {
            var name = Path.GetFileName(child);
            if (NestedExecutableDirectories.Contains(name))
            {
                directories.Add(child);
            }

            foreach (var architecture in WindowsBinaryDirectories)
            {
                directories.Add(Path.Combine(child, "Binaries", architecture));
            }

            if (name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var architecture in UnityPluginDirectories)
                {
                    directories.Add(Path.Combine(child, "Plugins", architecture));
                }
            }
        }

        var steamworksRoot = Path.Combine(searchRoot, "Engine", "Binaries", "ThirdParty", "Steamworks");
        foreach (var versionDirectory in EnumerateDirectoriesSafe(steamworksRoot))
        {
            foreach (var architecture in WindowsBinaryDirectories)
            {
                directories.Add(Path.Combine(versionDirectory, architecture));
            }
        }

        return directories
            .Select(Path.GetFullPath)
            .Where(path => IsWithin(searchRoot, path))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static void SearchBreadthFirst(
        string searchRoot,
        ICollection<string> found,
        ISet<string> seen)
    {
        var currentLevel = new List<string> { searchRoot };
        var visited = 0;

        for (var depth = 0; depth < MaxSearchDepth && currentLevel.Count > 0; depth++)
        {
            var nextLevel = new List<string>();

            foreach (var directory in currentLevel)
            {
                if (visited >= MaxDirectoriesVisited)
                {
                    return;
                }

                visited++;
                foreach (var child in EnumerateDirectoriesSafe(directory))
                {
                    if (!IsWithin(searchRoot, child) || IsReparsePoint(child))
                    {
                        continue;
                    }

                    var name = Path.GetFileName(child);
                    if (name.Equals("steam_settings", StringComparison.OrdinalIgnoreCase))
                    {
                        AddSettingsCandidate(searchRoot, child, found, seen);
                    }
                    else if (!IgnoredSearchDirectories.Contains(name))
                    {
                        nextLevel.Add(child);
                    }
                }
            }

            if (found.Count > 0)
            {
                return;
            }

            currentLevel = nextLevel;
        }
    }

    private static void AddSettingsCandidate(
        string searchRoot,
        string candidate,
        ICollection<string> found,
        ISet<string> seen)
    {
        if (!Directory.Exists(candidate) || !IsWithin(searchRoot, candidate) || IsReparsePoint(candidate))
        {
            return;
        }

        var fullPath = Path.GetFullPath(candidate);
        if (seen.Add(fullPath))
        {
            found.Add(fullPath);
        }
    }

    private static bool HasGameRootMarker(string directory)
    {
        foreach (var child in EnumerateDirectoriesSafe(directory))
        {
            var name = Path.GetFileName(child);
            if (RootMarkerDirectories.Contains(name) || name.EndsWith("_Data", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (RootMarkerFiles.Contains(Path.GetFileName(file)))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return false;
    }

    private static IReadOnlyList<string> EnumerateDirectoriesSafe(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? Directory.GetDirectories(directory)
                : Array.Empty<string>();
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            return (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsFileSystemRoot(string directory) =>
        string.Equals(
            Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetPathRoot(directory)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
