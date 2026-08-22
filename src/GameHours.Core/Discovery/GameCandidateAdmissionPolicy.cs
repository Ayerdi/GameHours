using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Discovery;

public static class GameCandidateAdmissionPolicy
{
    private static readonly HashSet<GameDetectionEvidenceKind> StrongEvidenceKinds =
    [
        GameDetectionEvidenceKind.InstalledGamePath,
        GameDetectionEvidenceKind.LearnedExecutablePath,
        GameDetectionEvidenceKind.WindowsGameConfigStore,
        GameDetectionEvidenceKind.UnrealRuntime,
        GameDetectionEvidenceKind.UnityRuntime
    ];

    public static bool ShouldRecord(
        ProcessSnapshot process,
        GameResolution resolution,
        double automaticTrackingThreshold = 0.80)
    {
        if (automaticTrackingThreshold is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(automaticTrackingThreshold));

        if (process.ProcessId == Environment.ProcessId ||
            string.IsNullOrWhiteSpace(process.ExecutablePath) ||
            resolution.IsHelperProcess ||
            resolution.Role.IsHelperLike() ||
            resolution.Confidence >= automaticTrackingThreshold)
            return false;

        var positiveEvidence = resolution.DetectionEvidence
            .Where(item => item.Weight > 0)
            .ToArray();
        if (positiveEvidence.Length == 0) return false;

        if (positiveEvidence.Any(item => StrongEvidenceKinds.Contains(item.Kind)))
            return true;

        // A generic desktop application can load Direct3D/OpenGL and own a visible window,
        // so those signals alone are far too noisy. Keep this fallback only for executables
        // placed in an explicitly game-oriented location; arbitrary paths remain available
        // through the manual "Add EXE" flow.
        return string.Equals(resolution.Method, "heuristic_graphics_candidate", StringComparison.Ordinal) &&
               positiveEvidence.Any(item => item.Kind == GameDetectionEvidenceKind.GraphicsRuntime) &&
               positiveEvidence.Any(item => item.Kind == GameDetectionEvidenceKind.VisibleWindow) &&
               IsGameOrientedPath(process.ExecutablePath);
    }

    public static bool IsGameOrientedPath(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return false;

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            if (string.IsNullOrWhiteSpace(directory)) return false;

            var segments = directory.Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            return segments.Any(segment =>
                segment.Equals("game", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("games", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("juego", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("juegos", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("gog games", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("steamapps", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("steam library", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("epic games", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(" games", StringComparison.OrdinalIgnoreCase) ||
                segment.EndsWith(" juegos", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }
}
