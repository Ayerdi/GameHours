using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHours.Desktop;

internal static class LocalAchievementImageService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

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

        ImageSource? loaded = TryLoadLocal(imageReference) ?? TryLoadTrustedSteamRemote(imageReference);

        lock (Gate)
        {
            Cache[imageReference] = loaded;
        }

        return loaded;
    }

    private static ImageSource? TryLoadLocal(string imagePath)
    {
        if (!File.Exists(imagePath))
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
            NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    private static ImageSource? TryLoadTrustedSteamRemote(string imageReference)
    {
        if (!Uri.TryCreate(imageReference, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !uri.Host.Equals("cdn.steamstatic.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.AbsolutePath.StartsWith(
                "/steamcommunity/public/images/apps/",
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        try
        {
            // WPF downloads remote BitmapImage sources asynchronously. Achievement state and
            // metadata remain fully local; artwork is optional and can stay blank while offline.
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = uri;
            bitmap.DecodePixelWidth = 64;
            bitmap.EndInit();
            return bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or ArgumentException or NotSupportedException or
            InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }
}
