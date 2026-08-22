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
        // Launcher discovery is dominated by filesystem/registry work and some sources expose
        // that work through an already-completed Task. Run each source independently so a slow
        // Steam library never serializes Epic/GOG discovery or blocks the caller's UI thread.
        var sourceTasks = _sources
            .Select(source => DiscoverSourceAsync(source, cancellationToken))
            .ToArray();
        var sourceResults = await Task.WhenAll(sourceTasks).ConfigureAwait(false);

        var byId = new Dictionary<Guid, DiscoveredGame>();
        foreach (var games in sourceResults)
        {
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

    private static Task<IReadOnlyList<DiscoveredGame>> DiscoverSourceAsync(
        IInstalledGameSource source,
        CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            try
            {
                return await source.DiscoverAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One launcher being broken or absent must not prevent discovery from others.
                return Array.Empty<DiscoveredGame>();
            }
        }, cancellationToken);
}
