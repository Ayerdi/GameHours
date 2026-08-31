using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopSingleInstanceLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "gamehours-single-instance-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void SecondLeaseIsRejectedUntilFirstLeaseIsReleased()
    {
        var path = Path.Combine(_root, "nested", "gamehours.instance.lock");
        var first = DesktopSingleInstanceLease.TryAcquire(path);

        Assert.NotNull(first);
        Assert.True(File.Exists(path));
        Assert.Null(DesktopSingleInstanceLease.TryAcquire(path));

        first.Dispose();

        using var next = DesktopSingleInstanceLease.TryAcquire(path);
        Assert.NotNull(next);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
