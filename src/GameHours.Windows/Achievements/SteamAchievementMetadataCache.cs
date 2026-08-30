using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Best-effort Steam metadata enrichment for Steam-compatible local achievement sources.
/// Unlock state, timestamps and progress always remain local-authoritative; this cache only
/// contributes presentation metadata such as localized names, descriptions and artwork.
/// </summary>
public sealed class SteamAchievementMetadataCache
{
    private const int CacheVersion = 2;
    private static readonly TimeSpan Freshness = TimeSpan.FromDays(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient HttpClient = new();
    private static readonly ConcurrentDictionary<string, Task<bool>> Refreshes =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cacheRoot;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly SteamCommunityAchievementPageClient _communityPageClient;

    public SteamAchievementMetadataCache()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHours",
                "cache",
                "steam-achievements"),
            () => DateTimeOffset.UtcNow)
    {
    }

    internal SteamAchievementMetadataCache(
        string cacheRoot,
        Func<DateTimeOffset>? utcNow = null,
        SteamCommunityAchievementPageClient? communityPageClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _communityPageClient = communityPageClient ?? new SteamCommunityAchievementPageClient();
    }

    public LocalAchievementSnapshot EnrichFromCache(LocalAchievementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!IsValidAppId(snapshot.AppId)) return snapshot;

        var document = TryReadDocument(snapshot.AppId!, CurrentSteamLanguage());
        if (document is null) return snapshot;

        return Enrich(snapshot, UsableMetadata(document));
    }

    /// <summary>
    /// Builds a presentation-only catalogue from a previously fetched Steam metadata document.
    /// This never contributes unlock state; scene-emulator files remain authoritative for that.
    /// </summary>
    public LocalAchievementSnapshot? TryReadCatalogueFromCache(string appId) =>
        TryReadCatalogueFromCache(appId, CurrentSteamLanguage());

    internal LocalAchievementSnapshot? TryReadCatalogueFromCache(string appId, string language)
    {
        if (!IsValidAppId(appId) || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var document = TryReadDocument(appId, language);
        if (document is null)
        {
            return null;
        }

        var metadata = UsableMetadata(document);
        if (metadata.Count == 0)
        {
            return null;
        }

        var achievements = metadata
            .Select(item => new LocalAchievement(
                item.ApiName,
                item.DisplayName,
                item.Description,
                item.Hidden,
                IsUnlocked: false,
                UnlockedAtUtc: null,
                item.IconUrl,
                item.LockedIconUrl ?? item.IconUrl,
                Progress: null,
                MaxProgress: null))
            .ToArray();

        return new LocalAchievementSnapshot(
            "Catálogo Steam en caché",
            appId,
            CachePath(appId, language),
            StatePath: null,
            achievements)
        {
            IsCatalogueComplete = true
        };
    }

    public Task<bool> EnsureFreshAsync(string appId)
    {
        if (!IsValidAppId(appId)) return Task.FromResult(false);

        var language = CurrentSteamLanguage();
        if (IsFresh(appId, language)) return Task.FromResult(false);

        var key = $"{_cacheRoot}|{language}|{appId}";
        return Refreshes.GetOrAdd(key, _ => RefreshAndReleaseAsync(key, appId, language));
    }

    internal static IReadOnlyList<SteamAchievementMetadata> ParseOfficialResponse(
        string json,
        string appId)
    {
        if (!IsValidAppId(appId) || string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<SteamAchievementMetadata>();
        }

        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("response", out var response) ||
            response.ValueKind != JsonValueKind.Object ||
            !response.TryGetProperty("achievements", out var achievements) ||
            achievements.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SteamAchievementMetadata>();
        }

        var results = new List<SteamAchievementMetadata>();
        foreach (var item in achievements.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            var apiName = ReadString(item, "internal_name");
            if (string.IsNullOrWhiteSpace(apiName)) continue;

            var displayName = ReadString(item, "localized_name");
            var description = ReadString(item, "localized_desc") ?? string.Empty;
            var icon = SteamAchievementIconResolver.TryBuildOfficialCdnUrl(
                appId,
                ReadString(item, "icon"));
            var lockedIcon = SteamAchievementIconResolver.TryBuildOfficialCdnUrl(
                appId,
                ReadString(item, "icon_gray"));

            results.Add(new SteamAchievementMetadata(
                apiName.Trim(),
                string.IsNullOrWhiteSpace(displayName) ? apiName.Trim() : displayName.Trim(),
                description.Trim(),
                ReadBoolean(item, "hidden"),
                icon,
                lockedIcon));
        }

        return results
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();
    }

    internal static LocalAchievementSnapshot Enrich(
        LocalAchievementSnapshot snapshot,
        IReadOnlyList<SteamAchievementMetadata> metadata)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);
        if (metadata.Count == 0) return snapshot;

        var byApiName = metadata
            .GroupBy(item => item.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var changed = false;
        var enriched = snapshot.Achievements
            .Select(achievement =>
            {
                if (!byApiName.TryGetValue(achievement.ApiName, out var remote))
                {
                    return achievement;
                }

                var updated = achievement with
                {
                    DisplayName = remote.DisplayName,
                    Description = remote.Description,
                    Hidden = remote.Hidden,
                    IconPath = remote.IconUrl ?? achievement.IconPath,
                    LockedIconPath = remote.LockedIconUrl ?? remote.IconUrl ?? achievement.LockedIconPath
                };
                changed |= updated != achievement;
                return updated;
            })
            .ToArray();

        return changed ? snapshot with { Achievements = enriched } : snapshot;
    }

    private static IReadOnlyList<SteamAchievementMetadata> UsableMetadata(
        SteamAchievementMetadataDocument document)
    {
        // Old metadata documents can contain artwork URLs that we now know may be stale 404s.
        // Keep their useful localized text, but do not let a non-empty broken URL suppress the
        // normal refresh path in the desktop view. The versioned refresh will repopulate artwork.
        return document.Version >= CacheVersion
            ? document.Achievements
            : document.Achievements
                .Select(achievement => achievement with { IconUrl = null, LockedIconUrl = null })
                .ToArray();
    }

    private async Task<bool> RefreshAndReleaseAsync(string key, string appId, string language)
    {
        try
        {
            return await RefreshCoreAsync(appId, language).ConfigureAwait(false);
        }
        finally
        {
            Refreshes.TryRemove(key, out _);
        }
    }

    private async Task<bool> RefreshCoreAsync(string appId, string language)
    {
        try
        {
            string json;
            using (var timeout = new CancellationTokenSource(RequestTimeout))
            {
                var url =
                    $"https://api.steampowered.com/IPlayerService/GetGameAchievements/v1/?appid={appId}&language={Uri.EscapeDataString(language)}";
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                using var response = await HttpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (!response.IsSuccessStatusCode) return false;
                json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            }

            IReadOnlyList<SteamAchievementMetadata> metadata = ParseOfficialResponse(json, appId);
            if (metadata.Count == 0) return false;

            // GetGameAchievements is still the authoritative metadata source, but newly published
            // games can temporarily expose icon references whose CDN object does not exist yet.
            // Steam's public achievements page is what users actually see, so prefer its real
            // image URL when it can be matched without changing names, descriptions or unlock state.
            var communityRows = await _communityPageClient
                .TryFetchAsync(appId, language)
                .ConfigureAwait(false);
            if (communityRows.Count > 0)
            {
                metadata = SteamCommunityAchievementPageClient.ApplyArtwork(metadata, communityRows);
            }

            var cache = new SteamAchievementMetadataDocument(
                appId,
                language,
                _utcNow().ToUniversalTime(),
                metadata,
                CacheVersion);
            await WriteAtomicallyAsync(CachePath(appId, language), cache, CancellationToken.None)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or JsonException or
            IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            PathTooLongException or NotSupportedException)
        {
            return false;
        }
    }

    private bool IsFresh(string appId, string language)
    {
        var document = TryReadDocument(appId, language);
        return document is not null &&
               document.Version >= CacheVersion &&
               _utcNow().ToUniversalTime() - document.FetchedAtUtc <= Freshness;
    }

    private SteamAchievementMetadataDocument? TryReadDocument(string appId, string language)
    {
        try
        {
            var path = CachePath(appId, language);
            if (!File.Exists(path)) return null;

            var document = JsonSerializer.Deserialize<SteamAchievementMetadataDocument>(
                File.ReadAllText(path),
                JsonOptions);
            return document is not null &&
                   string.Equals(document.AppId, appId, StringComparison.Ordinal) &&
                   string.Equals(document.Language, language, StringComparison.OrdinalIgnoreCase) &&
                   document.Achievements.Count > 0
                ? document
                : null;
        }
        catch (Exception exception) when (
            exception is JsonException or IOException or UnauthorizedAccessException or
            ArgumentException or InvalidOperationException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private string CachePath(string appId, string language) =>
        Path.Combine(_cacheRoot, language, $"{appId}.json");

    private static async Task WriteAtomicallyAsync(
        string path,
        SteamAchievementMetadataDocument document,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("Steam achievement cache path has no directory.");
        Directory.CreateDirectory(directory);

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(document, JsonOptions);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath)) File.Delete(tempPath);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
            {
            }
        }
    }

    private static string CurrentSteamLanguage()
    {
        var culture = CultureInfo.CurrentUICulture;
        if (culture.Name.Equals("es-419", StringComparison.OrdinalIgnoreCase)) return "latam";

        return culture.TwoLetterISOLanguageName.ToLowerInvariant() switch
        {
            "es" => "spanish",
            "de" => "german",
            "fr" => "french",
            "it" => "italian",
            "pt" when culture.Name.Contains("BR", StringComparison.OrdinalIgnoreCase) => "brazilian",
            "pt" => "portuguese",
            "ru" => "russian",
            "pl" => "polish",
            "tr" => "turkish",
            "ja" => "japanese",
            "ko" => "koreana",
            "zh" when culture.Name.Contains("TW", StringComparison.OrdinalIgnoreCase) ||
                      culture.Name.Contains("HK", StringComparison.OrdinalIgnoreCase) => "tchinese",
            "zh" => "schinese",
            _ => "english"
        };
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool ReadBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return false;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => property.TryGetInt32(out var value) && value != 0,
            _ => false
        };
    }

    private static bool IsValidAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.All(char.IsDigit);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}

internal sealed record SteamAchievementMetadata(
    string ApiName,
    string DisplayName,
    string Description,
    bool Hidden,
    string? IconUrl,
    string? LockedIconUrl);

internal sealed record SteamAchievementMetadataDocument(
    string AppId,
    string Language,
    DateTimeOffset FetchedAtUtc,
    IReadOnlyList<SteamAchievementMetadata> Achievements,
    int Version = 0);
