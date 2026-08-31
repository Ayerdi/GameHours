using System.Text.Json;

namespace GameHours.Desktop;

internal enum DesktopUpdateSourceKind
{
    Simple,
    GitHub
}

internal sealed record DesktopUpdateSourceSelection(
    DesktopUpdateSourceKind Kind,
    string Location);

internal static class DesktopUpdateSourcePolicy
{
    private sealed record BundledUpdateSourceDocument(
        string? Type,
        string? Repository);

    public static DesktopUpdateSourceSelection? Resolve(
        string? environmentSource,
        string? bundledConfiguration,
        string? legacyBundledSource)
    {
        if (!string.IsNullOrWhiteSpace(environmentSource))
        {
            var explicitSource = NormalizeExplicitOverride(environmentSource);
            return explicitSource is null
                ? null
                : new DesktopUpdateSourceSelection(DesktopUpdateSourceKind.Simple, explicitSource);
        }

        if (!string.IsNullOrWhiteSpace(bundledConfiguration))
        {
            return ParseBundledConfiguration(bundledConfiguration);
        }

        var legacySource = NormalizeBundledSource(legacyBundledSource);
        return legacySource is null
            ? null
            : new DesktopUpdateSourceSelection(DesktopUpdateSourceKind.Simple, legacySource);
    }

    internal static DesktopUpdateSourceSelection? ParseBundledConfiguration(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        BundledUpdateSourceDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<BundledUpdateSourceDocument>(
                json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (JsonException)
        {
            return null;
        }

        if (document is null ||
            !string.Equals(document.Type, "github", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var repository = NormalizeGitHubRepository(document.Repository);
        return repository is null
            ? null
            : new DesktopUpdateSourceSelection(DesktopUpdateSourceKind.GitHub, repository);
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

    internal static string? NormalizeGitHubRepository(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            !Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
            !uri.IsDefaultPort ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            return null;
        }

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 || segments.Any(string.IsNullOrWhiteSpace))
        {
            return null;
        }

        var repository = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        if (string.IsNullOrWhiteSpace(repository))
        {
            return null;
        }

        return $"https://github.com/{segments[0]}/{repository}";
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
