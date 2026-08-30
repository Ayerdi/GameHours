namespace GameHours.Windows.Achievements;

/// <summary>
/// Resolves known GSE/Goldberg achievement-state layouts for one Steam AppID.
/// Some releases add one account/profile directory below the AppID; discovery is deliberately
/// bounded to that single level so GameHours does not recursively scan arbitrary save trees.
/// </summary>
internal static class GseAchievementStatePathLocator
{
    private const int MaxNestedDirectories = 64;

    private static readonly string[] SaveFolderNames =
    {
        "GSE Saves",
        "Goldberg SteamEmu Saves"
    };

    public static IReadOnlyList<string> FindExisting(string appId, string? roamingRoot = null)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsDigit))
        {
            return Array.Empty<string>();
        }

        roamingRoot ??= Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(roamingRoot))
        {
            return Array.Empty<string>();
        }

        return SaveFolderNames
            .SelectMany(saveFolderName =>
                FindExistingInAppDirectory(Path.Combine(roamingRoot, saveFolderName, appId)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<string> FindExistingInAppDirectory(string appDirectory)
    {
        if (string.IsNullOrWhiteSpace(appDirectory))
        {
            return Array.Empty<string>();
        }

        var results = new List<string>();
        AddIfFile(results, Path.Combine(appDirectory, "achievements.json"));

        if (!Directory.Exists(appDirectory))
        {
            return results;
        }

        DirectoryInfo[] children;
        try
        {
            children = new DirectoryInfo(appDirectory)
                .GetDirectories()
                .OrderBy(directory => directory.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxNestedDirectories)
                .ToArray();
        }
        catch (IOException)
        {
            return results;
        }
        catch (UnauthorizedAccessException)
        {
            return results;
        }

        foreach (var child in children)
        {
            try
            {
                if ((child.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            AddIfFile(results, Path.Combine(child.FullName, "achievements.json"));
        }

        return results
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddIfFile(ICollection<string> results, string candidate)
    {
        if (File.Exists(candidate))
        {
            results.Add(Path.GetFullPath(candidate));
        }
    }
}
