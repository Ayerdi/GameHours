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
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/12c6dad8f2711dd4163b0ab9e005d0e94137e8bf.jpg";

        var first = await cache.EnsureCachedAsync(url);
        var second = await cache.EnsureCachedAsync(url);

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.True(File.Exists(first));
        Assert.Equal(payload, await File.ReadAllBytesAsync(first));
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(first, cache.TryGetCachedPath(url));
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
            "https://cdn.steamstatic.com/steamcommunity/public/images/apps/3946950/icon.jpg";

        var result = await cache.EnsureCachedAsync(url);

        Assert.Null(result);
        Assert.Equal(1, handler.RequestCount);
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

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(_responseFactory(request));
        }
    }
}
