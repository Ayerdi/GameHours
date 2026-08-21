namespace GameHours.Windows.Achievements;

/// <summary>
/// Resolves an achievement image only when Steam already has the exact schema-provided asset
/// name on disk. The resolver never guesses between arbitrary library artwork and never makes
/// a network request.
/// </summary>
internal static class SteamAchievementIconResolver
{
    private const int MaxHashDirectoriesToInspect = 128;

    public static string? TryResolve(
        string? steamRoot,
        string appId,
        string? assetName)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) ||
            string.IsNullOrWhiteSpace(appId) ||
            !appId.All(char.IsDigit) ||
            string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        try
        {
            var normalizedAssetName = NormalizeAssetName(assetName);
            if (normalizedAssetName is null)
            {
                return null;
            }

            var appCacheDirectory = Path.Combine(
                Path.GetFullPath(steamRoot),
                "appcache",
                "librarycache",
                appId);
            if (!Directory.Exists(appCacheDirectory))
            {
                return null;
            }

            var direct = Path.Combine(appCacheDirectory, normalizedAssetName);
            if (File.Exists(direct))
            {
                return Path.GetFullPath(direct);
            }

            // Modern Steam library artwork is often grouped below one additional hash/version
            // directory. Inspect only that bounded first level and still require the exact
            // schema asset filename, so unrelated game artwork can never be selected by guess.
            var inspected = 0;
            foreach (var directory in Directory.EnumerateDirectories(appCacheDirectory))
            {
                if (++inspected > MaxHashDirectoriesToInspect)
                {
                    break;
                }

                var candidate = Path.Combine(directory, normalizedAssetName);
                if (File.Exists(candidate))
                {
                    return Path.GetFullPath(candidate);
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            PathTooLongException or NotSupportedException)
        {
        }

        return null;
    }

    public static string? TryInferSteamRootFromSchemaPath(string schemaPath)
    {
        if (string.IsNullOrWhiteSpace(schemaPath))
        {
            return null;
        }

        try
        {
            var statsDirectory = Directory.GetParent(Path.GetFullPath(schemaPath));
            var appCacheDirectory = statsDirectory?.Parent;
            var steamRoot = appCacheDirectory?.Parent;
            return statsDirectory is not null &&
                   appCacheDirectory is not null &&
                   steamRoot is not null &&
                   statsDirectory.Name.Equals("stats", StringComparison.OrdinalIgnoreCase) &&
                   appCacheDirectory.Name.Equals("appcache", StringComparison.OrdinalIgnoreCase)
                ? steamRoot.FullName
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? NormalizeAssetName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 260)
        {
            return null;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            if (!uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
                !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            trimmed = Path.GetFileName(uri.AbsolutePath);
        }

        if (string.IsNullOrWhiteSpace(trimmed) ||
            !string.Equals(Path.GetFileName(trimmed), trimmed, StringComparison.Ordinal) ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return null;
        }

        return trimmed;
    }
}
