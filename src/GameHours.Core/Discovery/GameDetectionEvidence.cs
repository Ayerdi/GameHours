namespace GameHours.Core.Discovery;

public enum ExecutableRole
{
    Unknown = 0,
    PrimaryGame = 1,
    SecondaryGame = 2,
    Launcher = 3,
    AntiCheat = 4,
    Updater = 5,
    CrashHandler = 6,
    Helper = 7,
    Ignored = 8
}

public enum GameDetectionEvidenceKind
{
    InstalledGamePath = 1,
    LearnedExecutablePath = 2,
    WindowsGameConfigStore = 3,
    UnrealRuntime = 4,
    UnityRuntime = 5,
    GraphicsRuntime = 6,
    VisibleWindow = 7,
    ForegroundWindow = 8,
    FilenameHeuristic = 9,
    ExecutableRole = 10
}

public sealed record GameDetectionEvidence(
    GameDetectionEvidenceKind Kind,
    double Weight,
    string Detail);

public static class ExecutableRoleRules
{
    public static bool IsHelperLike(this ExecutableRole role) => role is
        ExecutableRole.Launcher or
        ExecutableRole.AntiCheat or
        ExecutableRole.Updater or
        ExecutableRole.CrashHandler or
        ExecutableRole.Helper or
        ExecutableRole.Ignored;

    public static bool IsTrackable(this ExecutableRole role) => role is
        ExecutableRole.PrimaryGame or
        ExecutableRole.SecondaryGame or
        ExecutableRole.Unknown;
}
