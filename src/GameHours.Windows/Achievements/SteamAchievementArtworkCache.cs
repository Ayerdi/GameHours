using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Small local cache for immutable achievement artwork referenced by Steam's official CDN.
/// Network access is explicit and on-demand; local achievement readers never call it.
/// </summary>
public sealed class SteamAchievementArtworkCache
{
    private const int MaxArtworkBytes = 2 * 1024 * 1024;
    private const int MaxConcurrentDownloads = 4;
    private const int MaxTrustedRedirects = 3;
    private const string ArtworkPathPrefix = "/steamcommunity/public/images/apps/";
    private const string AkamaiArtworkHost = "cdn.akamai.steamstatic.com";
    private const string CloudflareArtworkHost = "cdn.cloudflare.steamstatic.com";
    private const string LegacyArtworkHost = "cdn.steamstatic.com";
    private static readonly string[] FallbackHosts =
    {
        AkamaiArtworkHost,
        CloudflareArtworkHost,
        LegacyArtworkHost
    };
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient SharedHttpClient = new(new HttpClientHandler
    {
        // Redirects are followed manually so every hop can be constrained to Steam's
        // achievement-artwork CDN hosts and to the exact same immutable asset.
        AllowAutoRedirect = false
    });
    private static readonly SemaphoreSlim DownloadSlots = new(MaxConcurrentDownloads, MaxConcurrentDownloads);

    private readonly string _cacheRoot;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Task<string?>> _inFlight =
        new(StringComparer.OrdinalIgnoreCase);

    public SteamAchievementArtworkCache()
        : this(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHours",
                "cache",
                "steam-achievement-images"),
            SharedHttpClient)
    {
    }

    internal SteamAchievementArtworkCache(string cacheRoot, HttpClient httpClient)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheRoot);
        _cacheRoot = Path.GetFullPath(cacheRoot);
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public string? TryGetCachedPath(string? imageReference)
    {
        if (!TryResolve(imageReference, out _, out var cachePath))
        {
            return null;
        }

        return File.Exists(cachePath) ? cachePath : null;
    }

    public async Task<string?> EnsureCachedAsync(
        string? imageReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryResolve(imageReference, out var sourceUri, out var cachePath))
        {
            return null;
        }

        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        var task = _inFlight.GetOrAdd(
            cachePath,
            _ => DownloadAndCacheAsync(sourceUri, cachePath));
        try
        {
            return await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (task.IsCompleted)
            {
                _inFlight.TryRemove(cachePath, out _);
            }
        }
    }

    private async Task<string?> DownloadAndCacheAsync(Uri sourceUri, string cachePath)
    {
        await DownloadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!TryParseArtworkUri(sourceUri, out var appId, out var fileName))
            {
                return null;
            }

            foreach (var candidate in BuildCandidateUris(sourceUri))
            {
                var bytes = await TryDownloadArtworkAsync(candidate, appId, fileName).ConfigureAwait(false);
                if (bytes is null)
                {
                    continue;
                }

                var directory = Path.GetDirectoryName(cachePath)
                    ?? throw new InvalidOperationException("Achievement artwork cache path has no directory.");
                Directory.CreateDirectory(directory);

                var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
                    File.Move(tempPath, cachePath, overwrite: true);
                }
                finally
                {
                    TryDelete(tempPath);
                }

                return cachePath;
            }

            return null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or PathTooLongException or NotSupportedException)
        {
            return null;
        }
        finally
        {
            DownloadSlots.Release();
        }
    }

    private async Task<byte[]?> TryDownloadArtworkAsync(Uri initialUri, string appId, string fileName)
    {
        var currentUri = initialUri;
        for (var redirectCount = 0; redirectCount <= MaxTrustedRedirects; redirectCount++)
        {
            try
            {
                using var timeout = new CancellationTokenSource(RequestTimeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.Accept.ParseAdd("image/*");
                using var response = await _httpClient.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        timeout.Token)
                    .ConfigureAwait(false);

                if (IsRedirect(response.StatusCode))
                {
                    if (redirectCount == MaxTrustedRedirects ||
                        response.Headers.Location is not Uri location)
                    {
                        return null;
                    }

                    var redirected = location.IsAbsoluteUri
                        ? location
                        : new Uri(currentUri, location);
                    if (!TryParseArtworkUri(redirected, out var redirectedAppId, out var redirectedFileName) ||
                        !string.Equals(redirectedAppId, appId, StringComparison.Ordinal) ||
                        !string.Equals(redirectedFileName, fileName, StringComparison.Ordinal))
                    {
                        return null;
                    }

                    currentUri = redirected;
                    continue;
                }

                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentLength is > MaxArtworkBytes)
                {
                    return null;
                }

                var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
                return bytes.Length is > 0 and <= MaxArtworkBytes ? bytes : null;
            }
            catch (Exception exception) when (
                exception is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                return null;
            }
        }

        return null;
    }

    private static IEnumerable<Uri> BuildCandidateUris(Uri sourceUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (seen.Add(sourceUri.AbsoluteUri))
        {
            yield return sourceUri;
        }

        foreach (var host in FallbackHosts)
        {
            if (sourceUri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = new UriBuilder(sourceUri) { Host = host }.Uri;
            if (seen.Add(candidate.AbsoluteUri))
            {
                yield return candidate;
            }
        }
    }

    private bool TryResolve(
        string? imageReference,
        out Uri uri,
        out string cachePath)
    {
        uri = null!;
        cachePath = string.Empty;
        if (string.IsNullOrWhiteSpace(imageReference) ||
            !Uri.TryCreate(imageReference, UriKind.Absolute, out var parsed) ||
            !TryParseArtworkUri(parsed, out var appId, out var fileName))
        {
            return false;
        }

        try
        {
            var resolvedPath = Path.GetFullPath(Path.Combine(_cacheRoot, appId, fileName));
            var appRoot = Path.GetFullPath(Path.Combine(_cacheRoot, appId))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!resolvedPath.StartsWith(appRoot, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            uri = parsed;
            cachePath = resolvedPath;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool TryParseArtworkUri(Uri uri, out string appId, out string fileName)
    {
        appId = string.Empty;
        fileName = string.Empty;
        if (!uri.IsAbsoluteUri ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsTrustedArtworkHost(uri.Host) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            !uri.AbsolutePath.StartsWith(ArtworkPathPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            var relative = uri.AbsolutePath[ArtworkPathPrefix.Length..];
            var separator = relative.IndexOf('/');
            if (separator <= 0 || separator == relative.Length - 1 ||
                relative.IndexOf('/', separator + 1) >= 0)
            {
                return false;
            }

            appId = relative[..separator];
            fileName = Uri.UnescapeDataString(relative[(separator + 1)..]);
            return appId.All(char.IsDigit) &&
                   !string.IsNullOrWhiteSpace(fileName) &&
                   string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) &&
                   fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
        }
        catch (Exception exception) when (
            exception is ArgumentException or FormatException or NotSupportedException)
        {
            appId = string.Empty;
            fileName = string.Empty;
            return false;
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.Moved or
            HttpStatusCode.Redirect or
            HttpStatusCode.RedirectMethod or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsTrustedArtworkHost(string host) =>
        host.Equals(AkamaiArtworkHost, StringComparison.OrdinalIgnoreCase) ||
        host.Equals(CloudflareArtworkHost, StringComparison.OrdinalIgnoreCase) ||
        host.Equals(LegacyArtworkHost, StringComparison.OrdinalIgnoreCase);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
        }
    }
}
