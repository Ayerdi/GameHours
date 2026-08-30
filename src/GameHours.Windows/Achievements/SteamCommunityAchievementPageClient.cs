using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Best-effort public Steam Community fallback for achievement artwork.
/// The local emulator remains authoritative for unlock state; this client only resolves presentation metadata.
/// </summary>
internal sealed class SteamCommunityAchievementPageClient
{
    private const int MaxHtmlBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.GZip |
                                 DecompressionMethods.Deflate |
                                 DecompressionMethods.Brotli
    });

    private static readonly Regex RowStartRegex = new(
        "<div\\b[^>]*class\\s*=\\s*[\\\"'][^\\\"']*\\bachieveRow\\b[^\\\"']*[\\\"'][^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ImageRegex = new(
        "<img\\b(?<attrs>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex H3Regex = new(
        "<h3\\b[^>]*>(?<text>[\\s\\S]*?)</h3>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex H5Regex = new(
        "<h5\\b[^>]*>(?<text>[\\s\\S]*?)</h5>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;

    public SteamCommunityAchievementPageClient()
        : this(SharedHttpClient)
    {
    }

    internal SteamCommunityAchievementPageClient(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<IReadOnlyList<SteamCommunityAchievementRow>> TryFetchAsync(
        string appId,
        string language,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidAppId(appId))
        {
            return Array.Empty<SteamCommunityAchievementRow>();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var url =
                $"https://steamcommunity.com/stats/{appId}/achievements/?l={Uri.EscapeDataString(language)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("text/html,application/xhtml+xml");

            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxHtmlBytes)
            {
                return Array.Empty<SteamCommunityAchievementRow>();
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxHtmlBytes)
            {
                return Array.Empty<SteamCommunityAchievementRow>();
            }

            return Parse(System.Text.Encoding.UTF8.GetString(bytes), appId);
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or InvalidOperationException or
            ArgumentException or NotSupportedException)
        {
            return Array.Empty<SteamCommunityAchievementRow>();
        }
    }

    internal static IReadOnlyList<SteamCommunityAchievementRow> Parse(string html, string appId)
    {
        if (!IsValidAppId(appId) || string.IsNullOrWhiteSpace(html))
        {
            return Array.Empty<SteamCommunityAchievementRow>();
        }

        var starts = RowStartRegex.Matches(html);
        if (starts.Count == 0)
        {
            return Array.Empty<SteamCommunityAchievementRow>();
        }

        var rows = new List<SteamCommunityAchievementRow>(starts.Count);
        for (var index = 0; index < starts.Count; index++)
        {
            var start = starts[index].Index;
            var end = index + 1 < starts.Count ? starts[index + 1].Index : html.Length;
            if (end <= start) continue;

            var block = html[start..end];
            var title = ExtractText(H3Regex.Match(block));
            if (string.IsNullOrWhiteSpace(title)) continue;

            var imageMatch = ImageRegex.Match(block);
            if (!imageMatch.Success) continue;

            var attributes = imageMatch.Groups["attrs"].Value;
            var source = ReadAttribute(attributes, "src");
            var iconUrl = NormalizeArtworkUrl(source, appId);
            if (iconUrl is null) continue;

            var id = ReadAttribute(attributes, "id");
            var apiName = id?.StartsWith("iconImg", StringComparison.OrdinalIgnoreCase) == true
                ? id["iconImg".Length..].Trim()
                : null;
            if (string.IsNullOrWhiteSpace(apiName)) apiName = null;

            rows.Add(new SteamCommunityAchievementRow(
                apiName,
                title,
                ExtractText(H5Regex.Match(block)),
                iconUrl));
        }

        return rows;
    }

    internal static IReadOnlyList<SteamAchievementMetadata> ApplyArtwork(
        IReadOnlyList<SteamAchievementMetadata> metadata,
        IReadOnlyList<SteamCommunityAchievementRow> rows)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(rows);
        if (metadata.Count == 0 || rows.Count == 0) return metadata;

        var byApiName = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.ApiName))
            .GroupBy(row => row.ApiName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var byTitle = rows
            .Where(row => !string.IsNullOrWhiteSpace(row.DisplayName))
            .GroupBy(row => row.DisplayName.Trim(), StringComparer.CurrentCultureIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.CurrentCultureIgnoreCase);
        var canUsePosition = rows.Count == metadata.Count;

        return metadata
            .Select((achievement, index) =>
            {
                SteamCommunityAchievementRow? row = null;
                if (!byApiName.TryGetValue(achievement.ApiName, out row) &&
                    !byTitle.TryGetValue(achievement.DisplayName.Trim(), out row) &&
                    canUsePosition)
                {
                    row = rows[index];
                }

                return row is null
                    ? achievement
                    : achievement with
                    {
                        IconUrl = row.IconUrl,
                        // Steam Community exposes the real achievement icon but not a separate
                        // locked variant. A dimmed real icon is better than retaining a stale 404 URL.
                        LockedIconUrl = null
                    };
            })
            .ToArray();
    }

    private static string ExtractText(Match match)
    {
        if (!match.Success) return string.Empty;
        var withoutTags = TagRegex.Replace(match.Groups["text"].Value, string.Empty);
        return WebUtility.HtmlDecode(withoutTags).Replace('\u00a0', ' ').Trim();
    }

    private static string? ReadAttribute(string attributes, string name)
    {
        var match = Regex.Match(
            attributes,
            $"\\b{Regex.Escape(name)}\\s*=\\s*(?:\\\"(?<value>[^\\\"]*)\\\"|'(?<value>[^']*)')",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? WebUtility.HtmlDecode(match.Groups["value"].Value).Trim() : null;
    }

    private static string? NormalizeArtworkUrl(string? source, string appId)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;

        var value = source.Trim();
        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = "https:" + value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !IsTrustedSteamArtworkHost(uri.Host))
        {
            return null;
        }

        var expectedPrefix = $"/steamcommunity/public/images/apps/{appId}/";
        return uri.AbsolutePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    private static bool IsTrustedSteamArtworkHost(string host) =>
        host.Equals("steamcdn-a.akamaihd.net", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("cdn.steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("cdn.akamai.steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("cdn.cloudflare.steamstatic.com", StringComparison.OrdinalIgnoreCase);

    private static bool IsValidAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.All(char.IsDigit);
}

internal sealed record SteamCommunityAchievementRow(
    string? ApiName,
    string DisplayName,
    string Description,
    string IconUrl);
