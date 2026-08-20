namespace GameHours.Core.Monitoring;

public enum ProcessObservationType
{
    Started = 1,
    Stopped = 2,
    ReconciledStart = 3,
    ReconciledStop = 4,
    InitialSnapshot = 5
}

public sealed record ProcessObservation(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset OccurredAtUtc,
    ProcessObservationType Type);
