namespace GameHours.Desktop;

internal static class DesktopUpdateSourcePolicy
{
    public static string? Resolve(string? environmentSource, string? bundledSource)
    {
        if (!string.IsNullOrWhiteSpace(environmentSource))
        {
            return NormalizeExplicitOverride(environmentSource);
        }

        return NormalizeBundledSource(bundledSource);
    }

    internal static string? NormalizeExplicitOverride(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trimmed = source.Trim();
        if (Path.IsPathFullyQualified(trimmed))
        {
            return trimmed;
        }

        return IsHttpsSource(trimmed, allowQueryAndFragment: true)
            ? trimmed
            : null;
    }

    internal static string? NormalizeBundledSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return null;
        }

        var trimmed = source.Trim();
        return IsHttpsSource(trimmed, allowQueryAndFragment: false)
            ? trimmed
            : null;
    }

    private static bool IsHttpsSource(string source, bool allowQueryAndFragment)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return false;
        }

        return allowQueryAndFragment ||
               (string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment));
    }
}
