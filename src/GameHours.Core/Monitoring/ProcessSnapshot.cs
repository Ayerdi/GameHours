namespace GameHours.Core.Monitoring;

public sealed record ProcessSnapshot(
    int ProcessId,
    string ProcessName,
    string? ExecutablePath,
    DateTimeOffset? StartedAtUtc,
    int? ParentProcessId = null);
