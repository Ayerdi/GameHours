using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Abstractions;

public sealed record GameResolution(
    TrackedGame? Game,
    double Confidence,
    string Method,
    bool IsHelper = false);

public interface IGameResolver
{
    Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default);
}
