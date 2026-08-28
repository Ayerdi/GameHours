using System.Diagnostics;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Discovery;

public sealed class WindowsGameResolver : IGameResolver
{
    private readonly IReadOnlyList<DiscoveredGame> _installedGames;
    private readonly WindowsProcessEvidenceCollector _evidenceCollector;

    public WindowsGameResolver(IEnumerable<DiscoveredGame> installedGames, WindowsProcessEvidenceCollector? evidenceCollector = null)
    {
        _installedGames = installedGames?.OrderByDescending(game => game.InstallDirectory.Length).ToArray() ?? throw new ArgumentNullException(nameof(installedGames));
        _evidenceCollector = evidenceCollector ?? new WindowsProcessEvidenceCollector();
    }

    public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var executablePath = PathTools.Normalize(process.ExecutablePath);
        if (executablePath is null) return Task.FromResult(new GameResolution(null, 0, string.IsNullOrWhiteSpace(process.ExecutablePath) ? "missing_path" : "invalid_path"));
        if (IsKnownPlatformLauncher(executablePath))
            return Task.FromResult(new GameResolution(null, 0, "ignored_platform_launcher", true, ExecutableRole.Launcher));
        if (IsKnownPlatformInfrastructure(executablePath))
            return Task.FromResult(new GameResolution(null, 0, "ignored_platform_infrastructure", true, ExecutableRole.Helper));

        var assessment = _evidenceCollector.Collect(process with { ExecutablePath = executablePath });
        var role = assessment.Role;
        var helper = role.IsHelperLike();
        var installed = _installedGames.FirstOrDefault(game => IsInside(executablePath, game.InstallDirectory));
        if (installed is not null)
        {
            var evidence = assessment.Evidence.Append(new GameDetectionEvidence(GameDetectionEvidenceKind.InstalledGamePath, 0.90, $"Executable is inside {installed.Source} install directory")).ToArray();
            if (helper)
                return Task.FromResult(new GameResolution(installed.ToTrackedGame(), installed.Confidence, $"installed_{installed.Source.ToString().ToLowerInvariant()}_helper", true, role, evidence));

            var exactLaunch = IsLaunchExecutable(executablePath, installed);
            var strongRuntime = HasStrongInstalledRuntimeEvidence(assessment, role);
            if (exactLaunch || strongRuntime)
            {
                var installedRole = role == ExecutableRole.Unknown ? ExecutableRole.SecondaryGame : role;
                return Task.FromResult(new GameResolution(installed.ToTrackedGame(), installed.Confidence, exactLaunch ? $"installed_{installed.Source.ToString().ToLowerInvariant()}_launch_executable" : $"installed_{installed.Source.ToString().ToLowerInvariant()}_runtime", false, installedRole, evidence));
            }

            return Task.FromResult(new GameResolution(installed.ToTrackedGame(), 0.70, "installed_path_candidate", false, ExecutableRole.SecondaryGame, evidence));
        }

        if (helper) return Task.FromResult(new GameResolution(null, 0, "ignored_process_role", true, role, assessment.Evidence));
        if (assessment.IsInGameConfigStore && !IsWindowsSystemPath(executablePath)) return Task.FromResult(FromGameConfigStore(executablePath, assessment, role));
        if (IsWindowsSystemPath(executablePath)) return Task.FromResult(new GameResolution(null, 0, "ignored_windows_path", false, role, assessment.Evidence));

        var unreal = TryResolveUnreal(executablePath);
        if (unreal is not null) return Task.FromResult(unreal with { Role = ExecutableRole.PrimaryGame, Evidence = assessment.Evidence });
        var unity = TryResolveUnity(executablePath);
        if (unity is not null) return Task.FromResult(unity with { Role = ExecutableRole.PrimaryGame, Evidence = assessment.Evidence });

        if (IsProgramFilesPath(executablePath)) return Task.FromResult(new GameResolution(null, 0, "ignored_application_path", false, role, assessment.Evidence));
        if (assessment.HasGraphicsRuntime && assessment.HasVisibleWindow)
        {
            var directory = Path.GetDirectoryName(executablePath) ?? executablePath;
            var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(directory);
            return Task.FromResult(new GameResolution(new TrackedGame(DeterministicGameId.Create("heuristic-candidate", executablePath), title), 0.65, "heuristic_graphics_candidate", false, ExecutableRole.Unknown, assessment.Evidence));
        }
        return Task.FromResult(new GameResolution(null, 0, "unresolved", false, role, assessment.Evidence));
    }

    internal static bool HasStrongInstalledRuntimeEvidence(
        WindowsProcessEvidence assessment,
        ExecutableRole role)
    {
        if (role.IsHelperLike())
        {
            return false;
        }

        return role == ExecutableRole.PrimaryGame ||
               assessment.IsInGameConfigStore ||
               assessment.HasGraphicsRuntime ||
               assessment.Evidence.Any(item =>
                   item.Kind is GameDetectionEvidenceKind.UnrealRuntime or GameDetectionEvidenceKind.UnityRuntime);
    }

    private static GameResolution FromGameConfigStore(string path, WindowsProcessEvidence assessment, ExecutableRole role)
    {
        var confidence = 0.86 + (assessment.HasGraphicsRuntime ? 0.05 : 0) + (assessment.HasVisibleWindow ? 0.03 : 0) + (assessment.IsForegroundWindow ? 0.02 : 0);
        var title = GetProductTitle(path) ?? GetPathDerivedGameTitle(path);
        return new GameResolution(new TrackedGame(DeterministicGameId.Create("windows-gameconfig", path), title), Math.Min(confidence, 0.96), "windows_game_config_store", false, role == ExecutableRole.Unknown ? ExecutableRole.PrimaryGame : role, assessment.Evidence);
    }

    private static bool IsLaunchExecutable(string executablePath, DiscoveredGame game)
    {
        if (string.IsNullOrWhiteSpace(game.LaunchExecutable)) return false;
        var candidate = Path.IsPathRooted(game.LaunchExecutable) ? game.LaunchExecutable : Path.Combine(game.InstallDirectory, game.LaunchExecutable);
        var normalized = PathTools.Normalize(candidate);
        return normalized is not null && string.Equals(normalized, executablePath, StringComparison.OrdinalIgnoreCase);
    }

    private static GameResolution? TryResolveUnreal(string executablePath)
    {
        if (!IsUnrealShippingExecutable(executablePath)) return null;
        var installRoot = TryGetBinariesProjectRoot(executablePath);
        if (installRoot is null) return null;
        var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(installRoot);
        return new GameResolution(new TrackedGame(DeterministicGameId.Create("loose-unreal", installRoot), title), 0.95, "loose_unreal_shipping");
    }

    private static bool IsUnrealShippingExecutable(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        return fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryGetSteamInstallRoot(string executablePath)
    {
        var normalized = executablePath.Replace('/', '\\');
        const string marker = "\\steamapps\\common\\";
        var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return null;
        }

        var gameFolderStart = markerIndex + marker.Length;
        var separatorIndex = normalized.IndexOf('\\', gameFolderStart);
        return separatorIndex > gameFolderStart
            ? normalized[..separatorIndex]
            : null;
    }

    private static string? TryGetBinariesProjectRoot(string executablePath)
    {
        var normalized = executablePath.Replace('/', '\\');
        foreach (var marker in new[] { "\\Binaries\\Win64\\", "\\Binaries\\Win32\\" })
        {
            var markerIndex = normalized.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (markerIndex > 0)
            {
                return normalized[..markerIndex];
            }
        }

        return null;
    }

    private static string GetPathDerivedGameTitle(string executablePath)
    {
        var directory = TryGetSteamInstallRoot(executablePath)
            ?? TryGetBinariesProjectRoot(executablePath)
            ?? Path.GetDirectoryName(executablePath)
            ?? executablePath;
        return GetFriendlyFolderTitle(directory);
    }

    private static GameResolution? TryResolveUnity(string executablePath)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory)) return null;
        var baseName = Path.GetFileNameWithoutExtension(executablePath);
        if (!File.Exists(Path.Combine(directory, "UnityPlayer.dll")) && !Directory.Exists(Path.Combine(directory, $"{baseName}_Data"))) return null;
        var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(directory);
        return new GameResolution(new TrackedGame(DeterministicGameId.Create("loose-unity", directory), title), 0.90, "loose_unity_runtime");
    }

    private static bool IsInside(string executablePath, string installDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installDirectory));
            var path = Path.GetFullPath(executablePath);
            return string.Equals(path, root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static bool IsHelperExecutable(string executablePath) =>
        IsKnownPlatformLauncher(executablePath) ||
        IsKnownPlatformInfrastructure(executablePath) ||
        WindowsExecutableRoleClassifier.Classify(executablePath).IsHelperLike();

    private static bool IsKnownPlatformLauncher(string executablePath) =>
        Path.GetFileName(executablePath).Equals("GooglePlayGames.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownPlatformInfrastructure(string executablePath)
    {
        if (Path.GetFileName(executablePath).Equals("crosvm.exe", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var normalized = executablePath.Replace('/', '\\');
        return normalized.Contains("\\Google\\Play Games\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsSystemPath(string executablePath)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(root) && IsInside(executablePath, root);
    }

    private static bool IsProgramFilesPath(string executablePath) =>
        new[] { Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 }
            .Select(Environment.GetFolderPath)
            .Any(root => !string.IsNullOrWhiteSpace(root) && IsInside(executablePath, root));

    private static string? GetProductTitle(string executablePath)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(executablePath);
            return new[] { info.ProductName, info.FileDescription }.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate) && !candidate.Contains("shipping", StringComparison.OrdinalIgnoreCase) && !candidate.Equals("Unreal Engine", StringComparison.OrdinalIgnoreCase))?.Trim();
        }
        catch { return null; }
    }

    private static string GetFriendlyFolderTitle(string directory)
    {
        var normalized = Path.TrimEndingDirectorySeparator(directory);
        var leaf = Path.GetFileName(normalized);
        var parent = Directory.GetParent(normalized)?.Name;
        if (!string.IsNullOrWhiteSpace(parent) && leaf.Length <= 4 && parent.Length > leaf.Length && !parent.Equals("Games", StringComparison.OrdinalIgnoreCase)) return parent;
        return string.IsNullOrWhiteSpace(leaf) ? "Unknown game" : leaf;
    }
}
