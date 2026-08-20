using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using Microsoft.Win32;

namespace GameHours.Windows.Discovery;

public sealed class GogInstalledGameSource : IInstalledGameSource
{
    public string Name => "GOG";

    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<Guid, DiscoveredGame>();

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var gamesKey = baseKey.OpenSubKey(@"SOFTWARE\GOG.com\Games");
                if (gamesKey is null)
                {
                    continue;
                }

                foreach (var subKeyName in gamesKey.GetSubKeyNames())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    using var gameKey = gamesKey.OpenSubKey(subKeyName);
                    var installPath = gameKey?.GetValue("PATH") as string ?? gameKey?.GetValue("path") as string;
                    if (string.IsNullOrWhiteSpace(installPath) || !Directory.Exists(installPath))
                    {
                        continue;
                    }

                    var title = gameKey?.GetValue("GAMENAME") as string
                        ?? gameKey?.GetValue("gameName") as string
                        ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(installPath));
                    var gameId = gameKey?.GetValue("gameID")?.ToString();
                    var externalId = string.IsNullOrWhiteSpace(gameId) ? subKeyName : gameId;
                    if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(externalId))
                    {
                        continue;
                    }

                    var discovered = new DiscoveredGame(
                        DeterministicGameId.Create("gog", externalId),
                        title,
                        GameDiscoverySource.Gog,
                        externalId,
                        installPath,
                        null,
                        1.0);
                    results[discovered.GameId] = discovered;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (System.Security.SecurityException)
            {
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(results.Values.ToArray());
    }
}
