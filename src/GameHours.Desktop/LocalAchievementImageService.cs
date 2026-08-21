using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GameHours.Desktop;

internal static class LocalAchievementImageService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryLoad(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(imagePath, out var cached))
            {
                return cached;
            }
        }

        ImageSource? loaded;
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(Path.GetFullPath(imagePath), UriKind.Absolute);
            bitmap.DecodePixelWidth = 64;
            bitmap.EndInit();
            bitmap.Freeze();
            loaded = bitmap;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or InvalidOperationException)
        {
            loaded = null;
        }

        lock (Gate)
        {
            Cache[imagePath] = loaded;
        }

        return loaded;
    }
}
