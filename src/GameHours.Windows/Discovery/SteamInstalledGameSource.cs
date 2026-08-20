using System.Text.RegularExpressions;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using Microsoft.Win32;

namespace GameHours.Windows.Discovery;

public sealed partial class SteamInstalledGameSource : IInstalledGameSource
{
    public string Name => "Steam";

    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<Guid, DiscoveredGame>();

        foreach (var steamRoot in FindSteamRoots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var library in FindLibraries(steamRoot))
            {
                var steamApps = Path.Combine(library, "steamapps");
                if (!Directory.Exists(steamApps))
                {
                    continue;
                }

                foreach (var manifestPath in Directory.EnumerateFiles(steamApps, "appmanifest_*.acf"))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var game = TryReadManifest(library, manifestPath);
                    if (game is not null)
                    {
                        results[game.GameId] = game;
                    }
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(results.Values.ToArray());
    }

    private static DiscoveredGame? TryReadManifest(string library, string manifestPath)
    {
        try
        {
            var text = File.ReadAllText(manifestPath);
            var appId = GetValue(text, "appid");
            var name = GetValue(text, "name");
            var installDirName = GetValue(text, "installdir");
            if (string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDirName))
            {
                return null;
            }

            var installDirectory = Path.GetFullPath(Path.Combine(library, "steamapps", "common", installDirName));
            if (!Directory.Exists(installDirectory))
            {
                return null;
            }

            return new DiscoveredGame(
                DeterministicGameId.Create("steam", appId),
                name,
                GameDiscoverySource.Steam,
                appId,
                installDirectory,
                null,
                1.0);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static IEnumerable<string> FindSteamRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            if (key?.GetValue("SteamPath") is string steamPath && Directory.Exists(steamPath))
            {
                roots.Add(Path.GetFullPath(steamPath));
            }
        }
        catch
        {
        }

        var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var defaultRoot = Path.Combine(programFilesX86, "Steam");
        if (Directory.Exists(defaultRoot))
        {
            roots.Add(Path.GetFullPath(defaultRoot));
        }

        return roots;
    }

    private static IEnumerable<string> FindLibraries(string steamRoot)
    {
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.GetFullPath(steamRoot)
        };

        foreach (var path in new[]
                 {
                     Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"),
                     Path.Combine(steamRoot, "config", "libraryfolders.vdf")
                 })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                var text = File.ReadAllText(path);
                foreach (Match match in PathValueRegex().Matches(text))
                {
                    var value = UnescapeVdf(match.Groups[1].Value);
                    if (Directory.Exists(value))
                    {
                        libraries.Add(Path.GetFullPath(value));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return libraries;
    }

    private static string? GetValue(string text, string key)
    {
        var pattern = $"\\\"{Regex.Escape(key)}\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"";
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? UnescapeVdf(match.Groups[1].Value) : null;
    }

    private static string UnescapeVdf(string value) =>
        value.Replace("\\\\", "\\", StringComparison.Ordinal)
            .Replace("\\\"", "\"", StringComparison.Ordinal);

    [GeneratedRegex("\\\"path\\\"\\s*\\\"((?:\\\\.|[^\\\"])*)\\\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathValueRegex();
}
