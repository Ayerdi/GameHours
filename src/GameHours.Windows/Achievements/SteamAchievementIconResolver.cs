namespace GameHours.Windows.Achievements;

/// <summary>
/// Resolves Steam achievement artwork from the exact schema-provided asset name.
/// Local Steam cache files are preferred. When Steam has not cached the asset locally,
/// the resolver can derive the official Steam CDN URL without searching or guessing.
/// </summary>
internal static class SteamAchievementIconResolver
{
    private const int MaxHashDirectoriesToInspect = 128;
    private const string SteamArtworkBaseUrl =
        "https://cdn.steamstatic.com/steamcommunity/public/images/apps";

    public static string? TryResolve(
        string? steamRoot,
        string appId,
        string? assetName)
    {
        if (string.IsNullOrWhiteSpace(steamRoot) ||
            !IsValidAppId(appId) ||
            string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        try
        {
            var candidateNames = CandidateLocalAssetNames(assetName).ToArray();
            if (candidateNames.Length == 0)
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

            foreach (var candidateName in candidateNames)
            {
                var direct = Path.Combine(appCacheDirectory, candidateName);
                if (File.Exists(direct))
                {
                    return Path.GetFullPath(direct);
                }
            }

            // Modern Steam artwork is often grouped below one additional hash/version
            // directory. Inspect only that bounded first level and still require the exact
            // schema-derived filename, so unrelated game artwork can never be selected.
            var inspected = 0;
            foreach (var directory in Directory.EnumerateDirectories(appCacheDirectory))
            {
                if (++inspected > MaxHashDirectoriesToInspect)
                {
                    break;
                }

                foreach (var candidateName in candidateNames)
                {
                    var candidate = Path.Combine(directory, candidateName);
                    if (File.Exists(candidate))
                    {
                        return Path.GetFullPath(candidate);
                    }
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

    public static string? TryBuildOfficialCdnUrl(string appId, string? assetName)
    {
        if (!IsValidAppId(appId) || string.IsNullOrWhiteSpace(assetName))
        {
            return null;
        }

        var normalizedAssetName = NormalizeAssetName(assetName);
        if (normalizedAssetName is null)
        {
            return null;
        }

        return $"{SteamArtworkBaseUrl}/{appId}/{Uri.EscapeDataString(normalizedAssetName)}";
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

    private static IEnumerable<string> CandidateLocalAssetNames(string assetName)
    {
        var normalized = NormalizeAssetName(assetName);
        if (normalized is null)
        {
            yield break;
        }

        yield return normalized;

        // Some Steam schema/cache combinations expose the SHA-1 asset name without an
        // extension while librarycache stores the same exact hash as <hash>.jpg.
        if (Path.GetExtension(normalized).Length == 0 && IsSha1Hex(normalized))
        {
            yield return normalized + ".jpg";
        }
    }

    private static bool IsValidAppId(string? appId) =>
        !string.IsNullOrWhiteSpace(appId) && appId.All(char.IsDigit);

    private static bool IsSha1Hex(string value) =>
        value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string? NormalizeAssetName(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0 || trimmed.Length > 512)
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
