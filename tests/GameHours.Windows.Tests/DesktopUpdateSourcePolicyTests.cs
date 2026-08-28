using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopUpdateSourcePolicyTests
{
    [Fact]
    public void BundledSource_AcceptsPlainHttpsOrigin()
    {
        Assert.Equal(
            "https://updates.example.com/gamehours/beta",
            DesktopUpdateSourcePolicy.NormalizeBundledSource(" https://updates.example.com/gamehours/beta "));
    }

    [Theory]
    [InlineData("http://updates.example.com/gamehours")]
    [InlineData("https://user:secret@updates.example.com/gamehours")]
    [InlineData("https://updates.example.com/gamehours?token=secret")]
    [InlineData("https://updates.example.com/gamehours#beta")]
    [InlineData("relative/feed")]
    public void BundledSource_RejectsSourcesThatShouldNotShipInAPackage(string source)
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

    [Fact]
    public void InvalidExplicitOverride_FailsClosedInsteadOfFallingBackToBundledSource()
    {
        var result = DesktopUpdateSourcePolicy.Resolve(
            "http://unsafe.example.com/gamehours",
            "https://updates.example.com/gamehours");

        Assert.Null(result);
    }
}
