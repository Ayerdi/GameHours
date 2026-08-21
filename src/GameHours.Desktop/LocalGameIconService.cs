using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DrawingIcon = System.Drawing.Icon;

namespace GameHours.Desktop;

internal static class LocalGameIconService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static ImageSource? TryLoad(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(executablePath, out var cached))
            {
                return cached;
            }
        }

        var loaded = LoadCore(executablePath);
        lock (Gate)
        {
            Cache[executablePath] = loaded;
        }

        return loaded;
    }

    private static ImageSource? LoadCore(string executablePath)
    {
        if (!File.Exists(executablePath))
        {
            return null;
        }

        try
        {
            using var icon = DrawingIcon.ExtractAssociatedIcon(executablePath);
            if (icon is null)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle,
                Int32Rect.Empty,
                BitmapSizeOptions.FromWidthAndHeight(32, 32));
            source.Freeze();
            return source;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (ExternalException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }
}
