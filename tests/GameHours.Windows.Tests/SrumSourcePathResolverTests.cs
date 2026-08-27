namespace GameHours.Windows.Tests;

public sealed class SrumSourcePathResolverTests
{
    [Fact]
    public void ResolveFromCandidates_PrefersExplicitOverride()
    {
        var path = SrumSourcePathResolver.ResolveFromCandidates(
            @"D:\snapshots\SRUDB.dat",
            @"C:\Windows\System32",
            @"C:\Windows",
            @"C:\Windows",
            _ => true);

        Assert.Equal(Path.GetFullPath(@"D:\snapshots\SRUDB.dat"), path);
    }

    [Fact]
    public void ResolveFromCandidates_PrefersNativeSystemDirectoryWhenItExists()
    {
        var native = Path.Combine(@"C:\Windows\System32", "sru", "SRUDB.dat");
        var path = SrumSourcePathResolver.ResolveFromCandidates(
            null,
            @"C:\Windows\System32",
            @"C:\Windows",
            @"C:\Windows",
            candidate => string.Equals(candidate, native, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(native, path);
    }

    [Fact]
    public void ResolveFromCandidates_FallsBackToWindowsDirectoryCandidate()
    {
        var fallback = Path.Combine(@"D:\Windows", "System32", "sru", "SRUDB.dat");
        var path = SrumSourcePathResolver.ResolveFromCandidates(
            null,
            null,
            @"D:\Windows",
            null,
            candidate => string.Equals(candidate, fallback, StringComparison.OrdinalIgnoreCase));

        Assert.Equal(fallback, path);
    }
}
