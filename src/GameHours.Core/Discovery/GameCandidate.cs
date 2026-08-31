namespace GameHours.Core.Discovery;

public enum GameCandidateStatus
{
    Pending = 0,
    Resolved = 1,
    Ignored = 2
}

public sealed record GameCandidateObservation(
    string ExecutablePath,
    string ProcessName,
    string SuggestedTitle,
    double Confidence,
    string Method,
    ExecutableRole Role,
    IReadOnlyList<GameDetectionEvidence> Evidence,
    DateTimeOffset ObservedAtUtc)
{
    public string NormalizedExecutablePath => Path.GetFullPath(ExecutablePath);
}

public sealed record GameCandidate(
    string ExecutablePath,
    string ExecutableName,
    string ProcessName,
    string SuggestedTitle,
    double Confidence,
    string Method,
    ExecutableRole Role,
    IReadOnlyList<GameDetectionEvidence> Evidence,
    DateTimeOffset FirstSeenAtUtc,
    DateTimeOffset LastSeenAtUtc,
    int ObservationCount,
    GameCandidateStatus Status,
    ExecutableRole? DecisionRole = null,
    Guid? DecisionGameId = null,
    DateTimeOffset? ResolvedAtUtc = null);
