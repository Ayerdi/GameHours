using System.Collections.Concurrent;
using System.Net.Http;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Small local cache for achievement artwork referenced by Steam's official CDN.
/// Network access is explicit and on-demand; local achievement readers never call it.
/// </summary>
public sealed class SteamAchievementArtworkCache
{
    private const int MaxArtworkBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly HttpClient SharedHttpClient = new();

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
        if (!TryResolve(imageReference, out var uri, out var cachePath))
        {
            return null;
        }

        if (File.Exists(cachePath))
        {
            return cachePath;
        }

        var task = _inFlight.GetOrAdd(
            cachePath,
            _ => DownloadAndCacheAsync(uri, cachePath));
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

    private async Task<string?> DownloadAndCacheAsync(Uri uri, string cachePath)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode ||
                response.Content.Headers.ContentLength is > MaxArtworkBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            if (bytes.Length is 0 or > MaxArtworkBytes)
            {
                return null;
            }

            var directory = Path.GetDirectoryName(cachePath)
                ?? throw new InvalidOperationException("Achievement artwork cache path has no directory.");
            Directory.CreateDirectory(directory);

            var tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllBytesAsync(tempPath, bytes, timeout.Token).ConfigureAwait(false);
                File.Move(tempPath, cachePath, overwrite: true);
            }
            finally
            {
                TryDelete(tempPath);
            }

            return cachePath;
        }
        catch (Exception exception) when (
            exception is HttpRequestException or TaskCanceledException or IOException or
            UnauthorizedAccessException or ArgumentException or InvalidOperationException or
            PathTooLongException or NotSupportedException)
        {
            return null;
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
            !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !parsed.Host.Equals("cdn.steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            !parsed.AbsolutePath.StartsWith(
                "/steamcommunity/public/images/apps/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            const string prefix = "/steamcommunity/public/images/apps/";
            var relative = parsed.AbsolutePath[prefix.Length..];
            var separator = relative.IndexOf('/');
            if (separator <= 0 || separator == relative.Length - 1 ||
                relative.IndexOf('/', separator + 1) >= 0)
            {
                return false;
            }

            var appId = relative[..separator];
            var fileName = Uri.UnescapeDataString(relative[(separator + 1)..]);
            if (!appId.All(char.IsDigit) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
                fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }

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
