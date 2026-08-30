using System.Net;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamAchievementArtworkCacheTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-achievement-artwork-tests",
        Guid.NewGuid().ToString("N"));

    public SteamAchievementArtworkCacheTests()
    {
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureCachedAsync_TrustedSteamArtworkDownloadsOnceAndReusesDiskCache()
    {
        var payload = new byte[] { 1, 2, 3, 4, 5 };
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload)
        });
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);
        const string url =
            "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/3946950/12c6dad8f2711dd4163b0ab9e005d0e94137e8bf.jpg";

        var first = await cache.EnsureCachedAsync(url);
        var second = await cache.EnsureCachedAsync(url);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(payload, await File.ReadAllBytesAsync(first));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("cdn.akamai.steamstatic.com", Assert.Single(handler.RequestUris).Host);
        Assert.Equal(first, cache.TryGetCachedPath(url));
    }

    [Fact]
    public async Task EnsureCachedAsync_TrustedRedirectToSameSteamAssetIsFollowed()
    {
        var payload = new byte[] { 9, 8, 7 };
        var handler = new CountingHandler(request =>
        {
            if (request.RequestUri!.Host.Equals("cdn.steamstatic.com", StringComparison.OrdinalIgnoreCase))
            {
                return new HttpResponseMessage(HttpStatusCode.Redirect)
                {
                    Headers =
                    {
                        Location = new Uri(
                            "https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/3946950/redirected.jpg")
                    }
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        });
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);
        const string url =
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/redirected.jpg";

        var result = await cache.EnsureCachedAsync(url);

        Assert.NotNull(result);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result));
        Assert.Equal(2, handler.RequestCount);
        Assert.Equal("cdn.steamstatic.com", handler.RequestUris[0].Host);
        Assert.Equal("cdn.cloudflare.steamstatic.com", handler.RequestUris[1].Host);
    }

    [Fact]
    public async Task EnsureCachedAsync_UnavailableSteamHostFallsBackAcrossTrustedCdnHosts()
    {
        var payload = new byte[] { 4, 5, 6 };
        var handler = new CountingHandler(request =>
            request.RequestUri!.Host.Equals("cdn.cloudflare.steamstatic.com", StringComparison.OrdinalIgnoreCase)
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                }
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);
        const string legacyUrl =
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/fallback.jpg";

        var result = await cache.EnsureCachedAsync(legacyUrl);

        Assert.NotNull(result);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result));
        Assert.Equal(
            new[]
            {
                "cdn.steamstatic.com",
                "cdn.akamai.steamstatic.com",
                "cdn.cloudflare.steamstatic.com"
            },
            handler.RequestUris.Select(uri => uri.Host).ToArray());
    }

    [Fact]
    public async Task EnsureCachedAsync_RejectsRedirectOutsideTrustedSteamArtworkHosts()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.Redirect)
        {
            Headers =
            {
                Location = new Uri(
                    "https://example.com/steamcommunity/public/images/apps/3946950/icon.jpg")
            }
        });
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);
        const string url =
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/icon.jpg";

        var result = await cache.EnsureCachedAsync(url);

        Assert.Null(result);
        Assert.Equal(3, handler.RequestCount);
        Assert.All(handler.RequestUris, uri => Assert.EndsWith(".steamstatic.com", uri.Host));
        Assert.Null(cache.TryGetCachedPath(url));
    }

    [Fact]
    public async Task EnsureCachedAsync_RejectsUntrustedArtworkWithoutNetworkRequest()
    {
        var handler = new CountingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[] { 1 })
        });
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);

        var result = await cache.EnsureCachedAsync(
            "https://example.com/steamcommunity/public/images/apps/3946950/icon.jpg");

        Assert.Null(result);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task EnsureCachedAsync_RejectsOversizedArtworkWithoutWritingFile()
    {
        var handler = new CountingHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[] { 1 })
            };
            response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            return response;
        });
        using var httpClient = new HttpClient(handler);
        var cache = new SteamAchievementArtworkCache(_directory, httpClient);
        const string url =
            "https://cdn.akamai.steamstatic.com/steamcommunity/public/images/apps/3946950/icon.jpg";

        var result = await cache.EnsureCachedAsync(url);

        Assert.Null(result);
        Assert.Equal(3, handler.RequestCount);
        Assert.Null(cache.TryGetCachedPath(url));
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public CountingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int RequestCount { get; private set; }
        public List<Uri> RequestUris { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            if (request.RequestUri is Uri requestUri)
            {
                RequestUris.Add(requestUri);
            }

            return Task.FromResult(_responseFactory(request));
        }
    }
}
