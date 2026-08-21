using Microsoft.Win32;

namespace GameHours.Windows.Achievements;

public enum LocalAchievementSourceKind
{
    SteamLibraryCache,
    SteamSettingsDefinitions,
    Goldberg,
    Codex,
    Rune,
    OnlineFix,
    Empress,
    Rld,
    Skidrow,
    CreamApi,
    SmartSteamEmu,
    Rle,
    Razor1911,
    UserStats,
    ThreeDm,
    Ali213
}

public sealed record LocalAchievementSourceCandidate(
    LocalAchievementSourceKind Kind,
    string FilePath,
    string? AppId,
    string Scope);

/// <summary>
/// Locates achievement-related files on the local machine only.
/// This component never calls Steam, Hydra, or any other remote service.
/// </summary>
public sealed class LocalAchievementSourceLocator
{
    public IReadOnlyList<LocalAchievementSourceCandidate> Locate(
        string executablePath,
        string? appIdHint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);

        var executable = Path.GetFullPath(executablePath);
        var appId = NormalizeAppId(appIdHint) ?? TryReadAppIdNearExecutable(executable);
        var candidates = new List<LocalAchievementSourceCandidate>();

        LocateGameDirectorySources(executable, appId, candidates);

        if (appId is not null)
        {
            LocateGlobalSources(appId, candidates);
            LocateSteamLibraryCache(appId, candidates);
        }

        return candidates
            .Where(candidate => File.Exists(candidate.FilePath))
            .GroupBy(
                candidate => $"{candidate.Kind}\n{Path.GetFullPath(candidate.FilePath)}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(candidate => candidate.Kind)
            .ThenBy(candidate => candidate.FilePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void LocateGlobalSources(
        string appId,
        ICollection<LocalAchievementSourceCandidate> candidates)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var commonDocuments = Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        Add(candidates, LocalAchievementSourceKind.Goldberg,
            Path.Combine(appData, "GSE Saves", appId, "achievements.json"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.Goldberg,
            Path.Combine(appData, "Goldberg SteamEmu Saves", appId, "achievements.json"), appId, "roaming");

        Add(candidates, LocalAchievementSourceKind.Codex,
            Path.Combine(commonDocuments, "Steam", "CODEX", appId, "achievements.ini"), appId, "public_documents");
        Add(candidates, LocalAchievementSourceKind.Codex,
            Path.Combine(appData, "Steam", "CODEX", appId, "achievements.ini"), appId, "roaming");

        Add(candidates, LocalAchievementSourceKind.Rune,
            Path.Combine(commonDocuments, "Steam", "RUNE", appId, "achievements.ini"), appId, "public_documents");

        Add(candidates, LocalAchievementSourceKind.OnlineFix,
            Path.Combine(commonDocuments, "OnlineFix", appId, "Stats", "Achievements.ini"), appId, "public_documents");
        Add(candidates, LocalAchievementSourceKind.OnlineFix,
            Path.Combine(commonDocuments, "OnlineFix", appId, "Achievements.ini"), appId, "public_documents");

        Add(candidates, LocalAchievementSourceKind.Empress,
            Path.Combine(appData, "EMPRESS", "remote", appId, "achievements.json"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.Empress,
            Path.Combine(commonDocuments, "EMPRESS", appId, "remote", appId, "achievements.json"), appId, "public_documents");

        Add(candidates, LocalAchievementSourceKind.Rld,
            Path.Combine(programData, "RLD!", appId, "achievements.ini"), appId, "program_data");
        Add(candidates, LocalAchievementSourceKind.Rld,
            Path.Combine(programData, "Steam", "Player", appId, "stats", "achievements.ini"), appId, "program_data");
        Add(candidates, LocalAchievementSourceKind.Rld,
            Path.Combine(programData, "Steam", "RLD!", appId, "stats", "achievements.ini"), appId, "program_data");
        Add(candidates, LocalAchievementSourceKind.Rld,
            Path.Combine(programData, "Steam", "dodi", appId, "stats", "achievements.ini"), appId, "program_data");

        Add(candidates, LocalAchievementSourceKind.Skidrow,
            Path.Combine(documents, "SKIDROW", appId, "SteamEmu", "UserStats", "achiev.ini"), appId, "documents");
        Add(candidates, LocalAchievementSourceKind.Skidrow,
            Path.Combine(documents, "Player", appId, "SteamEmu", "UserStats", "achiev.ini"), appId, "documents");
        Add(candidates, LocalAchievementSourceKind.Skidrow,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SKIDROW", appId, "SteamEmu", "UserStats", "achiev.ini"), appId, "local_app_data");

        Add(candidates, LocalAchievementSourceKind.CreamApi,
            Path.Combine(appData, "CreamAPI", appId, "stats", "CreamAPI.Achievements.cfg"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.SmartSteamEmu,
            Path.Combine(appData, "SmartSteamEmu", appId, "User", "Achievements.ini"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.Rle,
            Path.Combine(appData, "RLE", appId, "achievements.ini"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.Rle,
            Path.Combine(appData, "RLE", appId, "Achievements.ini"), appId, "roaming");
        Add(candidates, LocalAchievementSourceKind.Razor1911,
            Path.Combine(appData, ".1911", appId, "achievement"), appId, "roaming");
    }

    private static void LocateSteamLibraryCache(
        string appId,
        ICollection<LocalAchievementSourceCandidate> candidates)
    {
        foreach (var steamRoot in FindSteamRoots())
        {
            var userdata = Path.Combine(steamRoot, "userdata");
            if (!Directory.Exists(userdata))
            {
                continue;
            }

            string[] userDirectories;
            try
            {
                userDirectories = Directory.GetDirectories(userdata);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var userDirectory in userDirectories)
            {
                Add(candidates, LocalAchievementSourceKind.SteamLibraryCache,
                    Path.Combine(userDirectory, "config", "librarycache", $"{appId}.json"),
                    appId,
                    "steam_userdata");
            }
        }
    }

    private static void LocateGameDirectorySources(
        string executablePath,
        string? appId,
        ICollection<LocalAchievementSourceCandidate> candidates)
    {
        foreach (var root in EnumerateAncestorDirectories(executablePath, maxDepth: 6))
        {
            Add(candidates, LocalAchievementSourceKind.SteamSettingsDefinitions,
                Path.Combine(root, "steam_settings", "achievements.json"), appId, "game_directory");
            Add(candidates, LocalAchievementSourceKind.UserStats,
                Path.Combine(root, "SteamData", "user_stats.ini"), appId, "game_directory");

            AddProfileMatches(
                candidates,
                LocalAchievementSourceKind.ThreeDm,
                Path.Combine(root, "3DMGAME"),
                new[] { "stats", "achievements.ini" },
                appId);

            AddProfileMatches(
                candidates,
                LocalAchievementSourceKind.Ali213,
                Path.Combine(root, "Profile"),
                new[] { "Stats", "Achievements.Bin" },
                appId);

            var steamSettings = Path.Combine(root, "steam_settings");
            if (Directory.Exists(steamSettings))
            {
                string[] appDirectories;
                try
                {
                    appDirectories = Directory.GetDirectories(steamSettings);
                }
                catch (IOException)
                {
                    appDirectories = Array.Empty<string>();
                }
                catch (UnauthorizedAccessException)
                {
                    appDirectories = Array.Empty<string>();
                }

                foreach (var appDirectory in appDirectories)
                {
                    var name = Path.GetFileName(appDirectory);
                    if (name.Length == 0 || !name.All(char.IsDigit))
                    {
                        continue;
                    }

                    Add(candidates, LocalAchievementSourceKind.Goldberg,
                        Path.Combine(appDirectory, "achievements.json"), name, "game_directory");
                }
            }
        }
    }

    private static void AddProfileMatches(
        ICollection<LocalAchievementSourceCandidate> candidates,
        LocalAchievementSourceKind kind,
        string profilesDirectory,
        IReadOnlyList<string> relativeSegments,
        string? appId)
    {
        if (!Directory.Exists(profilesDirectory))
        {
            return;
        }

        string[] profiles;
        try
        {
            profiles = Directory.GetDirectories(profilesDirectory);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        foreach (var profile in profiles.Take(64))
        {
            Add(candidates, kind,
                Path.Combine(new[] { profile }.Concat(relativeSegments).ToArray()),
                appId,
                "game_directory");
        }
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

    private static string? TryReadAppIdNearExecutable(string executablePath)
    {
        foreach (var root in EnumerateAncestorDirectories(executablePath, maxDepth: 6))
        {
            foreach (var candidate in new[]
                     {
                         Path.Combine(root, "steam_appid.txt"),
                         Path.Combine(root, "steam_settings", "steam_appid.txt")
                     })
            {
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    var value = NormalizeAppId(File.ReadAllText(candidate));
                    if (value is not null)
                    {
                        return value;
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

        return null;
    }

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
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

    private static void Add(
        ICollection<LocalAchievementSourceCandidate> candidates,
        LocalAchievementSourceKind kind,
        string path,
        string? appId,
        string scope)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        candidates.Add(new LocalAchievementSourceCandidate(kind, path, appId, scope));
    }
}
