using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Core.Discovery;

public sealed class InstalledGameDiscoveryService
{
    private readonly IReadOnlyList<IInstalledGameSource> _sources;

    public InstalledGameDiscoveryService(IEnumerable<IInstalledGameSource> sources)
    {
        _sources = sources?.ToArray() ?? throw new ArgumentNullException(nameof(sources));
    }

    public async Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var byId = new Dictionary<Guid, DiscoveredGame>();

        foreach (var source in _sources)
        {
            IReadOnlyList<DiscoveredGame> games;
            try
            {
                games = await source.DiscoverAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // One launcher being broken or absent must not prevent discovery from others.
                continue;
            }

            foreach (var game in games)
            {
                if (!byId.TryGetValue(game.GameId, out var existing) ||
                    game.Confidence > existing.Confidence)
                {
                    byId[game.GameId] = game;
                }
            }
        }

        return byId.Values
            .OrderBy(game => game.Title, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
