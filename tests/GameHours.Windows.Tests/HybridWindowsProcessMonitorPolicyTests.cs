using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;
using GameHours.Windows.Processes;

namespace GameHours.Windows.Tests;

public sealed class HybridWindowsProcessMonitorPolicyTests
{
    [Fact]
    public void Diagnostics_BeforeObservation_ArePassiveAndEmpty()
    {
        var monitor = new HybridWindowsProcessMonitor(new EmptySnapshotProvider());

        var diagnostics = monitor.GetDiagnostics();

        Assert.False(diagnostics.IsRunning);
        Assert.False(diagnostics.EventDrivenActive);
        Assert.False(diagnostics.DegradedFallback);
        Assert.Equal(0, diagnostics.ProcessStartEvents);
        Assert.Equal(0, diagnostics.FullReconciliations);
        Assert.Null(diagnostics.LastReconciliationAtUtc);
    }

    [Fact]
    public void ReconciledStart_UsesProcessStartWhenItFallsInsideReconciliationWindow()
    {
        var previous = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var started = previous.AddSeconds(2);
        var observed = previous.AddSeconds(5);
        var process = new ProcessSnapshot(42, "game", @"C:\Games\game.exe", started);

        var actual = HybridWindowsProcessMonitor.GetReconciledStartAt(process, previous, observed);

        Assert.Equal(started, actual);
    }

    [Fact]
    public void ReconciledStart_DoesNotPredateLastCompleteSnapshot()
    {
        var previous = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var observed = previous.AddSeconds(5);
        var process = new ProcessSnapshot(42, "game", @"C:\Games\game.exe", previous.AddMinutes(-1));

        var actual = HybridWindowsProcessMonitor.GetReconciledStartAt(process, previous, observed);

        Assert.Equal(previous, actual);
    }

    [Fact]
    public void ReconciledStart_WithoutReliableStartTime_UsesObservationTime()
    {
        var previous = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var observed = previous.AddSeconds(5);
        var process = new ProcessSnapshot(42, "game", @"C:\Games\game.exe", null);

        var actual = HybridWindowsProcessMonitor.GetReconciledStartAt(process, previous, observed);

        Assert.Equal(observed, actual);
    }

    [Fact]
    public void ReconciledStart_ClampsImpossibleFutureStartToObservation()
    {
        var previous = new DateTimeOffset(2026, 8, 23, 10, 0, 0, TimeSpan.Zero);
        var observed = previous.AddSeconds(5);
        var process = new ProcessSnapshot(42, "game", @"C:\Games\game.exe", observed.AddMinutes(1));

        var actual = HybridWindowsProcessMonitor.GetReconciledStartAt(process, previous, observed);

        Assert.Equal(observed, actual);
    }

    private sealed class EmptySnapshotProvider : IProcessSnapshotProvider
    {
        public Task<IReadOnlyList<ProcessSnapshot>> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProcessSnapshot>>(Array.Empty<ProcessSnapshot>());
    }
}
