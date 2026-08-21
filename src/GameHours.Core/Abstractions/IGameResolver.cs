using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Abstractions;

public sealed record GameResolution(
    TrackedGame? Game,
    double Confidence,
    string Method,
    bool IsHelper = false,
    ExecutableRole Role = ExecutableRole.Unknown,
    IReadOnlyList<GameDetectionEvidence>? Evidence = null)
{
    public IReadOnlyList<GameDetectionEvidence> DetectionEvidence =>
        Evidence ?? Array.Empty<GameDetectionEvidence>();

    public bool IsHelperProcess => IsHelper || Role.IsHelperLike();
}

public interface IGameResolver
{
    Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default);
}
