using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHours.Windows.Achievements;

namespace GameHours.Desktop;

internal static class LocalAchievementImageService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SteamAchievementArtworkCache SteamArtworkCache = new();

    public static ImageSource? TryLoad(string? imageReference)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(imageReference, out var cached))
            {
                return cached;
            }
        }

        var loaded = TryLoadLocal(imageReference);
        if (loaded is null)
        {
            loaded = TryLoadLocal(SteamArtworkCache.TryGetCachedPath(imageReference));
        }

        if (loaded is not null)
        {
            lock (Gate)
            {
                Cache[imageReference] = loaded;
            }
        }

        return loaded;
    }

    public static Task<ImageSource?> LoadAsync(
        string? imageReference,
        CancellationToken cancellationToken = default)
    {
        var loaded = TryLoad(imageReference);
        if (loaded is not null || string.IsNullOrWhiteSpace(imageReference))
        {
            return Task.FromResult(loaded);
        }

        return LoadRemoteAsync(imageReference, cancellationToken);
    }

    private static async Task<ImageSource?> LoadRemoteAsync(
        string imageReference,
        CancellationToken cancellationToken)
    {
        var localPath = await SteamArtworkCache
            .EnsureCachedAsync(imageReference, cancellationToken)
            .ConfigureAwait(false);
        var loaded = TryLoadLocal(localPath);
        if (loaded is not null)
        {
            lock (Gate)
            {
                Cache[imageReference] = loaded;
            }
        }

        return loaded;
    }

    private static ImageSource? TryLoadLocal(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(imagePath), UriKind.Absolute);
            bitmap.DecodePixelWidth = 64;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidOperationException or FormatException or PathTooLongException)
        {
            return null;
        }
    }
}
