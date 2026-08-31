using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopUpdateSourcePolicyTests
{
    [Fact]
    public void BundledGitHubConfiguration_ResolvesCanonicalPublicRepository()
    {
        var result = DesktopUpdateSourcePolicy.ParseBundledConfiguration(
            """
            {
              "type": "github",
              "repository": "https://github.com/Ayerdi/GameHours/"
            }
            """);

        Assert.Equal(DesktopUpdateSourceKind.GitHub, result?.Kind);
        Assert.Equal("https://github.com/Ayerdi/GameHours", result?.Location);
    }

    [Theory]
    [InlineData("{not-json")]
    [InlineData("{\"type\":\"unknown\",\"repository\":\"https://github.com/Ayerdi/GameHours\"}")]
    [InlineData("{\"type\":\"github\",\"repository\":\"http://github.com/Ayerdi/GameHours\"}")]
    [InlineData("{\"type\":\"github\",\"repository\":\"https://example.com/Ayerdi/GameHours\"}")]
    [InlineData("{\"type\":\"github\",\"repository\":\"https://github.com/Ayerdi/GameHours/releases\"}")]
    [InlineData("{\"type\":\"github\",\"repository\":\"https://user:secret@github.com/Ayerdi/GameHours\"}")]
    public void BundledGitHubConfiguration_RejectsMalformedOrUnsafeSources(string source)
    {
        Assert.Null(DesktopUpdateSourcePolicy.ParseBundledConfiguration(source));
    }

    [Fact]
    public void ExplicitOverride_WinsOverBundledGitHubConfiguration()
    {
        var localFeed = Path.Combine(Path.GetTempPath(), "GameHours", "feed");

        var result = DesktopUpdateSourcePolicy.Resolve(
            localFeed,
            "{\"type\":\"github\",\"repository\":\"https://github.com/Ayerdi/GameHours\"}",
            null);

        Assert.Equal(DesktopUpdateSourceKind.Simple, result?.Kind);
        Assert.Equal(localFeed, result?.Location);
    }

    [Fact]
    public void InvalidExplicitOverride_FailsClosedInsteadOfFallingBackToBundledConfiguration()
    {
        var result = DesktopUpdateSourcePolicy.Resolve(
            "http://unsafe.example.com/gamehours",
            "{\"type\":\"github\",\"repository\":\"https://github.com/Ayerdi/GameHours\"}",
            null);

        Assert.Null(result);
    }

    [Fact]
    public void MalformedBundledConfiguration_FailsClosedInsteadOfFallingBackToLegacySource()
    {
        var result = DesktopUpdateSourcePolicy.Resolve(
            null,
            "{not-json",
            "https://updates.example.com/gamehours");

        Assert.Null(result);
    }

    [Fact]
    public void LegacyBundledSource_RemainsSupportedWhenNoTypedConfigurationExists()
    {
        var result = DesktopUpdateSourcePolicy.Resolve(
            null,
            null,
            "https://updates.example.com/gamehours/beta");

        Assert.Equal(DesktopUpdateSourceKind.Simple, result?.Kind);
        Assert.Equal("https://updates.example.com/gamehours/beta", result?.Location);
    }

    [Theory]
    [InlineData("http://updates.example.com/gamehours")]
    [InlineData("https://user:secret@updates.example.com/gamehours")]
    [InlineData("https://updates.example.com/gamehours?token=secret")]
    [InlineData("https://updates.example.com/gamehours#beta")]
    [InlineData("relative/feed")]
    public void LegacyBundledSource_RejectsSourcesThatShouldNotShipInAPackage(string source)
    {
        Assert.Null(DesktopUpdateSourcePolicy.NormalizeBundledSource(source));
    }

    [Fact]
    public void ExplicitOverride_AllowsFullyQualifiedLocalFeedForVelopackTesting()
    {
        var source = Path.Combine(Path.GetTempPath(), "GameHours", "feed");

        Assert.Equal(source, DesktopUpdateSourcePolicy.NormalizeExplicitOverride(source));
    }

    [Fact]
    public void ExplicitOverride_AllowsHttpsQueryBecauseItIsNotBundled()
    {
        const string source = "https://updates.example.com/gamehours?test=1";

        Assert.Equal(source, DesktopUpdateSourcePolicy.NormalizeExplicitOverride(source));
    }

    [Fact]
    public void ExplicitOverride_RejectsHttpAndRelativePaths()
    {
        Assert.Null(DesktopUpdateSourcePolicy.NormalizeExplicitOverride("http://updates.example.com/gamehours"));
        Assert.Null(DesktopUpdateSourcePolicy.NormalizeExplicitOverride("relative/feed"));
    }
}
