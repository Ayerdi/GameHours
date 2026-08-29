using System.Text.Json;

namespace GameHours.Windows.Achievements;

public enum GseAchievementCatalogueProvisioningStatus
{
    NotApplicable,
    AlreadyPresent,
    AppIdUnavailable,
    CatalogueUnavailable,
    Created,
    Failed
}

public sealed record GseAchievementCatalogueProvisioningResult(
    GseAchievementCatalogueProvisioningStatus Status,
    string? AppId = null,
    string? DefinitionPath = null,
    int AchievementCount = 0);

/// <summary>
/// Creates the minimal achievements.json that GSE/Goldberg needs to persist future unlocks.
/// Existing emulator metadata is never overwritten. The provisioner does not write user state
/// and does not infer unlocks from the remote catalogue.
/// </summary>
public sealed class GseAchievementCatalogueProvisioner
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly Func<string, CancellationToken, Task<IReadOnlyList<string>?>> _fetchAchievementNames;

    public GseAchievementCatalogueProvisioner()
        : this(new SteamGlobalAchievementNameClient().FetchAsync)
    {
    }

    internal GseAchievementCatalogueProvisioner(
        Func<string, CancellationToken, Task<IReadOnlyList<string>?>> fetchAchievementNames)
    {
        _fetchAchievementNames = fetchAchievementNames
            ?? throw new ArgumentNullException(nameof(fetchAchievementNames));
    }

    public async Task<GseAchievementCatalogueProvisioningResult> TryProvisionAsync(
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new(GseAchievementCatalogueProvisioningStatus.NotApplicable);
        }

        try
        {
            var executable = Path.GetFullPath(executablePath);
            var settingsDirectory = GseInstallationDetector.FindSettingsDirectory(executable);
            if (settingsDirectory is null)
            {
                return new(GseAchievementCatalogueProvisioningStatus.NotApplicable);
            }

            var searchRoot = SteamSettingsDirectoryLocator.ResolveGameSearchRoot(executable);
            if (!SteamSettingsDirectoryLocator.IsWithin(searchRoot, settingsDirectory))
            {
                return new(GseAchievementCatalogueProvisioningStatus.NotApplicable);
            }

            var definitionPath = Path.Combine(settingsDirectory, "achievements.json");
            if (File.Exists(definitionPath))
            {
                return new(
                    GseAchievementCatalogueProvisioningStatus.AlreadyPresent,
                    GseRuntimeAchievementStateLocator.TryReadAppId(executable, settingsDirectory),
                    definitionPath);
            }

            var appId = GseRuntimeAchievementStateLocator.TryReadAppId(executable, settingsDirectory);
            if (appId is null)
            {
                return new(
                    GseAchievementCatalogueProvisioningStatus.AppIdUnavailable,
                    DefinitionPath: definitionPath);
            }

            var names = await _fetchAchievementNames(appId, cancellationToken).ConfigureAwait(false);
            if (names is null)
            {
                return new(
                    GseAchievementCatalogueProvisioningStatus.CatalogueUnavailable,
                    appId,
                    definitionPath);
            }

            var definitions = names
                .Select(name => name?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => new GseAchievementDefinition(
                    name,
                    name,
                    string.Empty,
                    Hidden: "0"))
                .ToArray();

            if (definitions.Length == 0)
            {
                return new(
                    GseAchievementCatalogueProvisioningStatus.CatalogueUnavailable,
                    appId,
                    definitionPath);
            }

            var status = await WriteNewCatalogueAsync(
                settingsDirectory,
                definitionPath,
                definitions,
                cancellationToken).ConfigureAwait(false);

            return new(
                status,
                appId,
                definitionPath,
                status == GseAchievementCatalogueProvisioningStatus.Created ? definitions.Length : 0);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or JsonException or PathTooLongException or NotSupportedException)
        {
            return new(GseAchievementCatalogueProvisioningStatus.Failed);
        }
    }

    private static async Task<GseAchievementCatalogueProvisioningStatus> WriteNewCatalogueAsync(
        string settingsDirectory,
        string definitionPath,
        IReadOnlyList<GseAchievementDefinition> definitions,
        CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(
            settingsDirectory,
            $".gamehours-achievements-{Guid.NewGuid():N}.tmp");

        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 4096,
                             useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    definitions,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            try
            {
                File.Move(temporaryPath, definitionPath);
                return GseAchievementCatalogueProvisioningStatus.Created;
            }
            catch (IOException) when (File.Exists(definitionPath))
            {
                return GseAchievementCatalogueProvisioningStatus.AlreadyPresent;
            }
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed record GseAchievementDefinition(
        string Name,
        string DisplayName,
        string Description,
        string Hidden);
}
