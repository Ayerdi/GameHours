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

    public WindowsGameResolver(
        IEnumerable<DiscoveredGame> installedGames,
        WindowsProcessEvidenceCollector? evidenceCollector = null)
    {
        _installedGames = installedGames?
            .OrderByDescending(game => game.InstallDirectory.Length)
            .ToArray()
            ?? throw new ArgumentNullException(nameof(installedGames));
        _evidenceCollector = evidenceCollector ?? new WindowsProcessEvidenceCollector();
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

        var observed = process with { ExecutablePath = executablePath };
        var assessment = _evidenceCollector.Collect(observed);
        var role = assessment.Role;
        var helper = role.IsHelperLike();

        var installed = _installedGames.FirstOrDefault(game => IsInside(executablePath, game.InstallDirectory));
        if (installed is not null)
        {
            var installedEvidence = assessment.Evidence
                .Append(new GameDetectionEvidence(
                    GameDetectionEvidenceKind.InstalledGamePath,
                    0.90,
                    $"Executable is inside {installed.Source} install directory"))
                .ToArray();
            var installedRole = helper
                ? role
                : role == ExecutableRole.Unknown
                    ? ExecutableRole.SecondaryGame
                    : role;

            return Task.FromResult(new GameResolution(
                installed.ToTrackedGame(),
                installed.Confidence,
                $"installed_{installed.Source.ToString().ToLowerInvariant()}_path",
                helper,
                installedRole,
                installedEvidence));
        }

        if (helper)
        {
            return Task.FromResult(new GameResolution(
                null,
                0,
                "ignored_process_role",
                true,
                role,
                assessment.Evidence));
        }

        if (assessment.IsInGameConfigStore && !IsWindowsSystemPath(executablePath))
        {
            var confidence = 0.86;
            if (assessment.HasGraphicsRuntime)
            {
                confidence += 0.05;
            }

            if (assessment.HasVisibleWindow)
            {
                confidence += 0.03;
            }

            if (assessment.IsForegroundWindow)
            {
                confidence += 0.02;
            }

            var directory = Path.GetDirectoryName(executablePath) ?? executablePath;
            var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(directory);
            var game = new TrackedGame(
                DeterministicGameId.Create("windows-gameconfig", executablePath),
                title);

            return Task.FromResult(new GameResolution(
                game,
                Math.Min(confidence, 0.96),
                "windows_game_config_store",
                false,
                role == ExecutableRole.Unknown ? ExecutableRole.PrimaryGame : role,
                assessment.Evidence));
        }

        if (IsSystemOrApplicationPath(executablePath))
        {
            return Task.FromResult(new GameResolution(
                null,
                0,
                "ignored_process_path",
                false,
                role,
                assessment.Evidence));
        }

        var unreal = TryResolveUnreal(executablePath);
        if (unreal is not null)
        {
            return Task.FromResult(unreal with
            {
                Role = ExecutableRole.PrimaryGame,
                Evidence = assessment.Evidence
            });
        }

        var unity = TryResolveUnity(executablePath);
        if (unity is not null)
        {
            return Task.FromResult(unity with
            {
                Role = ExecutableRole.PrimaryGame,
                Evidence = assessment.Evidence
            });
        }

        // Graphics/window evidence is useful for the future unresolved-candidate UI, but is
        // deliberately kept below the automatic tracking threshold without a stronger identity
        // source such as a launcher manifest, GameConfigStore or a known engine layout.
        if (assessment.HasGraphicsRuntime && assessment.HasVisibleWindow)
        {
            var directory = Path.GetDirectoryName(executablePath) ?? executablePath;
            var title = GetProductTitle(executablePath) ?? GetFriendlyFolderTitle(directory);
            return Task.FromResult(new GameResolution(
                new TrackedGame(
                    DeterministicGameId.Create("heuristic-candidate", executablePath),
                    title),
                0.65,
                "heuristic_graphics_candidate",
                false,
                ExecutableRole.Unknown,
                assessment.Evidence));
        }

        return Task.FromResult(new GameResolution(
            null,
            0,
            "unresolved",
            false,
            role,
            assessment.Evidence));
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

    public static bool IsHelperExecutable(string executablePath) =>
        WindowsExecutableRoleClassifier.Classify(executablePath).IsHelperLike();

    private static bool IsWindowsSystemPath(string executablePath)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return !string.IsNullOrWhiteSpace(root) && IsInside(executablePath, root);
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
