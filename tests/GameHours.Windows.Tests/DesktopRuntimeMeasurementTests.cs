using GameHours.Desktop;
using GameHours.Windows.Processes;

namespace GameHours.Windows.Tests;

public sealed class DesktopRuntimeMeasurementTests
{
    [Fact]
    public void Calculate_AggregatesCpuMemoryThreadsAndReconciliationsOverSameInterval()
    {
        var samples = new[]
        {
            Snapshot(TimeSpan.FromSeconds(10), 100, 200, 10, 4),
            Snapshot(TimeSpan.FromSeconds(10.5), 120, 240, 14, 5),
            Snapshot(TimeSpan.FromSeconds(11), 110, 220, 12, 7)
        };

        var result = DesktopRuntimeMeasurementSampler.Calculate(
            samples,
            TimeSpan.FromSeconds(10),
            processorCount: 2);

        Assert.Equal(5d, result.CpuPercent);
        Assert.Equal(110, result.AveragePrivateMemoryBytes);
        Assert.Equal(120, result.PeakPrivateMemoryBytes);
        Assert.Equal(220, result.AverageWorkingSetBytes);
        Assert.Equal(240, result.PeakWorkingSetBytes);
        Assert.Equal(12d, result.AverageThreadCount);
        Assert.Equal(14, result.PeakThreadCount);
        Assert.Equal(3, result.ReconciliationDelta);
    }

    [Fact]
    public void Calculate_IgnoresUnavailablePointMetricsInsteadOfFabricatingZeros()
    {
        var samples = new[]
        {
            Snapshot(TimeSpan.FromSeconds(3), 0, 0, 0, 8),
            Snapshot(TimeSpan.FromSeconds(3), 128, 256, 16, 9)
        };

        var result = DesktopRuntimeMeasurementSampler.Calculate(
            samples,
            TimeSpan.FromSeconds(5),
            processorCount: 4);

        Assert.Equal(0d, result.CpuPercent);
        Assert.Equal(128, result.AveragePrivateMemoryBytes);
        Assert.Equal(128, result.PeakPrivateMemoryBytes);
        Assert.Equal(256, result.AverageWorkingSetBytes);
        Assert.Equal(256, result.PeakWorkingSetBytes);
        Assert.Equal(16d, result.AverageThreadCount);
        Assert.Equal(16, result.PeakThreadCount);
        Assert.Equal(1, result.ReconciliationDelta);
    }

    [Fact]
    public void Calculate_MonitorRestartDoesNotInventReconciliationDelta()
    {
        var samples = new[]
        {
            Snapshot(TimeSpan.FromSeconds(1), 100, 200, 10, 12),
            Snapshot(TimeSpan.FromSeconds(2), 110, 210, 11, 2)
        };

        var result = DesktopRuntimeMeasurementSampler.Calculate(
            samples,
            TimeSpan.FromSeconds(10),
            processorCount: 4);

        Assert.Null(result.ReconciliationDelta);
    }

    private static DesktopRuntimeDiagnostics Snapshot(
        TimeSpan cpu,
        long privateMemory,
        long workingSet,
        int threadCount,
        long reconciliations)
    {
        return new DesktopRuntimeDiagnostics(
            IsTracking: true,
            StatusText: "Monitorizando juegos",
            ActiveGameTitle: null,
            Preferences: DesktopPreferences.Default,
            AppliedAfkTimeoutMinutes: DesktopPreferences.DefaultAfkTimeoutMinutes,
            ProcessMonitor: new WindowsProcessMonitorDiagnostics(
                IsRunning: true,
                EventDrivenActive: true,
                DegradedFallback: false,
                ProcessStartEvents: 0,
                FullReconciliations: reconciliations,
                LastReconciliationAtUtc: DateTimeOffset.UtcNow),
            ProcessCpuTime: cpu,
            PrivateMemoryBytes: privateMemory,
            WorkingSetBytes: workingSet,
            ThreadCount: threadCount,
            DatabasePath: "gamehours.db",
            PreferencesPath: "settings.json");
    }
}
