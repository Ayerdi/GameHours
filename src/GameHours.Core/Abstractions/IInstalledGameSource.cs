using GameHours.Core.Domain;

namespace GameHours.Core.Abstractions;

public interface IInstalledGameSource
{
    string Name { get; }

    Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        CancellationToken cancellationToken = default);
}
