using System.Diagnostics;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Discovery;

public sealed class WindowsGameResolver : IGameResolver
{
    private readonly IReadOnlyList<DiscoveredGame> _installedGames;

    public WindowsGameResolver(IEnumerable<DiscoveredGame> installedGames)
    {
        _installedGames = installedGames?
            .OrderByDescending(game => game.InstallDirectory.Length)
            .ToArray()
            ?? throw new ArgumentNullException(nameof(installedGames));
    }

    public Task<GameResolution> ResolveAsync(
        ProcessSnapshot process,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return Task.FromResult(new GameResolution(null, 0, "missing_path"));
        }

        string executablePath;
        try
        {
            executablePath = Path.GetFullPath(process.ExecutablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Task.FromResult(new GameResolution(null, 0, "invalid_path"));
        }

        var helper = IsHelperExecutable(executablePath);
        var installed = _installedGames.FirstOrDefault(game => IsInside(executablePath, game.InstallDirectory));
        if (installed is not null)
        {
            return Task.FromResult(new GameResolution(
                installed.ToTrackedGame(),
                installed.Confidence,
                $"installed_{installed.Source.ToString().ToLowerInvariant()}_path",
                helper));
        }

        if (helper || IsSystemOrApplicationPath(executablePath))
        {
            return Task.FromResult(new GameResolution(null, 0, "ignored_process", helper));
        }

        var unreal = TryResolveUnreal(executablePath);
        if (unreal is not null)
        {
            return Task.FromResult(unreal);
        }

        var unity = TryResolveUnity(executablePath);
        if (unity is not null)
        {
            return Task.FromResult(unity);
        }

        return Task.FromResult(new GameResolution(null, 0, "unresolved"));
    }

    private static GameResolution? TryResolveUnreal(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        if (!fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) &&
            !fileName.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var normalized = executablePath.Replace('/', '\\');
        var marker = normalized.Contains("\\Binaries\\Win64\\", StringComparison.OrdinalIgnoreCase)
            ? "\\Binaries\\Win64\\"
            : "\\Binaries\\Win32\\";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex <= 0)
        {
            return null;
        }

        var installRoot = normalized[..markerIndex];
        var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(installRoot);
        var game = new TrackedGame(
            DeterministicGameId.Create("loose-unreal", installRoot),
            title);
        return new GameResolution(game, 0.95, "loose_unreal_shipping");
    }

    private static GameResolution? TryResolveUnity(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        var baseName = Path.GetFileNameWithoutExtension(executablePath);
        var hasUnityPlayer = File.Exists(Path.Combine(directory, "UnityPlayer.dll"));
        var hasDataDirectory = Directory.Exists(Path.Combine(directory, $"{baseName}_Data"));
        if (!hasUnityPlayer && !hasDataDirectory)
        {
            return null;
        }

        var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(directory);
        var game = new TrackedGame(
            DeterministicGameId.Create("loose-unity", directory),
            title);
        return new GameResolution(game, 0.90, "loose_unity_runtime");
    }

    private static bool IsInside(string executablePath, string installDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            var path = Path.GetFullPath(executablePath);
            if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public static bool IsHelperExecutable(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        if (fileName.Equals("CrashReportClient.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("UnityCrashHandler64.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("UnityCrashHandler32.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("steam.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("steamwebhelper.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.Equals("EpicWebHelper.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
        return nameWithoutExtension.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.EndsWith("Updater", StringComparison.OrdinalIgnoreCase)
            || nameWithoutExtension.StartsWith("Unins", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSystemOrApplicationPath(string executablePath)
    {
        foreach (var specialFolder in new[]
                 {
                     Environment.SpecialFolder.Windows,
                     Environment.SpecialFolder.ProgramFiles,
                     Environment.SpecialFolder.ProgramFilesX86
                 })
        {
            var root = Environment.GetFolderPath(specialFolder);
            if (!string.IsNullOrWhiteSpace(root) && IsInside(executablePath, root))
            {
                return true;
            }
        }

        return false;
    }

    private static string? GetProductTitle(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            foreach (var candidate in new[] { info.ProductName, info.FileDescription })
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !candidate.Contains("shipping", StringComparison.OrdinalIgnoreCase) &&
                    !candidate.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase))
                {
                    return candidate.Trim();
                }
            }
        }
        catch
        {
        }

        return null;
    }

    private static string GetFriendlyFolderTitle(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(directory);
        var leaf = Path.GetFileName(normalized);
        var parent = Directory.GetParent(normalized)?.Name;

        if (!string.IsNullOrWhiteSpace(parent) &&
            leaf.Length <= 4 &&
            parent.Length > leaf.Length &&
            !parent.Equals("Games", StringComparison.OrdinalIgnoreCase))
        {
            return parent;
        }

        return string.IsNullOrWhiteSpace(leaf) ? "Unknown game" : leaf;
    }
}
