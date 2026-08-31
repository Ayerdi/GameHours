using System.Text.Json;
using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using Microsoft.Win32;

namespace GameHours.Windows.Discovery;

public sealed class EpicInstalledGameSource : IInstalledGameSource
{
    public string Name => "Epic";

    public Task<IReadOnlyList<DiscoveredGame>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<Guid, DiscoveredGame>();

        foreach (var manifestDirectory in FindManifestDirectories())
        {
            if (!Directory.Exists(manifestDirectory))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(manifestDirectory, "*.item"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var game = TryReadManifest(manifestPath);
                if (game is not null)
                {
                    results[game.GameId] = game;
                }
            }
        }

        return Task.FromResult<IReadOnlyList<DiscoveredGame>>(results.Values.ToArray());
    }

    private static DiscoveredGame? TryReadManifest(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            if (!IsGame(root))
            {
                return null;
            }

            var title = GetString(root, "DisplayName");
            var installLocation = GetString(root, "InstallLocation");
            var appName = GetString(root, "AppName");
            var catalogItemId = GetString(root, "CatalogItemId");
            var launchExecutable = GetString(root, "LaunchExecutable");
            var externalId = !string.IsNullOrWhiteSpace(catalogItemId) ? catalogItemId : appName;

            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(installLocation) ||
                string.IsNullOrWhiteSpace(externalId) ||
                !Directory.Exists(installLocation))
            {
                return null;
            }

            return new DiscoveredGame(
                DeterministicGameId.Create("epic", externalId),
                title,
                GameDiscoverySource.Epic,
                externalId,
                installLocation,
                launchExecutable,
                1.0);
        }
        catch (JsonException)
        {
            return null;
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

    private static bool IsGame(JsonElement root)
    {
        if (root.TryGetProperty("bIsApplication", out var application) &&
            application.ValueKind is JsonValueKind.False)
        {
            return false;
        }

        if (root.TryGetProperty("AppCategories", out var categories) &&
            categories.ValueKind is JsonValueKind.Array)
        {
            return categories.EnumerateArray()
                .Any(category => string.Equals(category.GetString(), "games", StringComparison.OrdinalIgnoreCase));
        }

        var technicalType = GetString(root, "TechnicalType");
        return technicalType?.Contains("games", StringComparison.OrdinalIgnoreCase) is true;
    }

    private static string? GetString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) && value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static IEnumerable<string> FindManifestDirectories()
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        directories.Add(Path.Combine(programData, "Epic", "EpicGamesLauncher", "Data", "Manifests"));

        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = baseKey.OpenSubKey(@"SOFTWARE\Epic Games\EpicGamesLauncher");
                if (key?.GetValue("AppDataPath") is string appDataPath && !string.IsNullOrWhiteSpace(appDataPath))
                {
                    directories.Add(Path.Combine(appDataPath, "Manifests"));
                }
            }
            catch
            {
            }
        }

        return directories;
    }
}
