using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopSaveSafetyOperationLifetimeTests : IDisposable
{
    public DesktopSaveSafetyOperationLifetimeTests()
    {
        DesktopSaveSafetyOperationLifetime.Resume();
    }

    [Fact]
    public async Task StopAndWaitAsync_BlocksNewOperationsAndWaitsForActiveLease()
    {
        Assert.True(DesktopSaveSafetyOperationLifetime.TryBegin(out var lease));
        Assert.NotNull(lease);

        var stop = DesktopSaveSafetyOperationLifetime.StopAndWaitAsync();

        Assert.False(stop.IsCompleted);
        Assert.False(DesktopSaveSafetyOperationLifetime.TryBegin(out _));

        lease!.Dispose();
        await stop;
    }

    [Fact]
    public async Task Resume_AllowsOperationsAfterAStoppedIdleLifetime()
    {
        await DesktopSaveSafetyOperationLifetime.StopAndWaitAsync();
        Assert.False(DesktopSaveSafetyOperationLifetime.TryBegin(out _));

        DesktopSaveSafetyOperationLifetime.Resume();

        Assert.True(DesktopSaveSafetyOperationLifetime.TryBegin(out var lease));
        lease!.Dispose();
    }

    public void Dispose()
    {
        DesktopSaveSafetyOperationLifetime.Resume();
    }
}
