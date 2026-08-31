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
    public void Calculate_ManagedMemoryUsesSameMeasurementInterval()
    {
        var samples = new[]
        {
            Snapshot(
                TimeSpan.FromSeconds(1),
                100,
                200,
                10,
                4,
                managedHeap: 1_000,
                totalAllocated: 10_000,
                gen0Collections: 5,
                gen1Collections: 2,
                gen2Collections: 1,
                gcCommitted: 4_000,
                gcFragmented: 100,
                gcTotalPause: TimeSpan.FromMilliseconds(100)),
            Snapshot(
                TimeSpan.FromSeconds(1.5),
                110,
                210,
                11,
                5,
                managedHeap: 1_500,
                totalAllocated: 16_000,
                gen0Collections: 8,
                gen1Collections: 3,
                gen2Collections: 1,
                gcCommitted: 5_000,
                gcFragmented: 250,
                gcTotalPause: TimeSpan.FromMilliseconds(130))
        };

        var result = DesktopRuntimeMeasurementSampler.Calculate(
            samples,
            TimeSpan.FromSeconds(3),
            processorCount: 4);

        Assert.Equal(1_250, result.AverageManagedHeapBytes);
        Assert.Equal(1_500, result.PeakManagedHeapBytes);
        Assert.Equal(2_000d, result.ManagedAllocationRateBytesPerSecond);
        Assert.Equal(5_000, result.PeakGcCommittedBytes);
        Assert.Equal(250, result.PeakGcFragmentedBytes);
        Assert.Equal(1d, result.GcPausePercent);
        Assert.Equal(3, result.Gen0CollectionDelta);
        Assert.Equal(1, result.Gen1CollectionDelta);
        Assert.Equal(0, result.Gen2CollectionDelta);
    }

    [Fact]
    public void Calculate_ManagedCountersRollingBackDoNotInventRatesOrCollectionDeltas()
    {
        var samples = new[]
        {
            Snapshot(
                TimeSpan.FromSeconds(1),
                100,
                200,
                10,
                4,
                managedHeap: 1_000,
                totalAllocated: 20_000,
                gen0Collections: 8,
                gen1Collections: 4,
                gen2Collections: 2,
                gcTotalPause: TimeSpan.FromSeconds(2)),
            Snapshot(
                TimeSpan.FromSeconds(2),
                110,
                210,
                11,
                5,
                managedHeap: 1_200,
                totalAllocated: 10_000,
                gen0Collections: 2,
                gen1Collections: 1,
                gen2Collections: 0,
                gcTotalPause: TimeSpan.FromSeconds(1))
        };

        var result = DesktopRuntimeMeasurementSampler.Calculate(
            samples,
            TimeSpan.FromSeconds(10),
            processorCount: 4);

        Assert.Null(result.ManagedAllocationRateBytesPerSecond);
        Assert.Null(result.GcPausePercent);
        Assert.Null(result.Gen0CollectionDelta);
        Assert.Null(result.Gen1CollectionDelta);
        Assert.Null(result.Gen2CollectionDelta);
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
        Assert.Null(result.AverageManagedHeapBytes);
        Assert.Null(result.PeakManagedHeapBytes);
        Assert.Equal(0d, result.ManagedAllocationRateBytesPerSecond);
        Assert.Null(result.PeakGcCommittedBytes);
        Assert.Equal(0, result.PeakGcFragmentedBytes);
        Assert.Equal(0d, result.GcPausePercent);
        Assert.Equal(0, result.Gen0CollectionDelta);
        Assert.Equal(0, result.Gen1CollectionDelta);
        Assert.Equal(0, result.Gen2CollectionDelta);
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
        long reconciliations,
        long managedHeap = 0,
        long totalAllocated = 0,
        int gen0Collections = 0,
        int gen1Collections = 0,
        int gen2Collections = 0,
        long gcCommitted = 0,
        long gcFragmented = 0,
        TimeSpan? gcTotalPause = null)
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
            ManagedHeapBytes: managedHeap,
            TotalAllocatedBytes: totalAllocated,
            Gen0CollectionCount: gen0Collections,
            Gen1CollectionCount: gen1Collections,
            Gen2CollectionCount: gen2Collections,
            GcCommittedBytes: gcCommitted,
            GcFragmentedBytes: gcFragmented,
            GcTotalPauseDuration: gcTotalPause ?? TimeSpan.Zero,
            DatabasePath: "gamehours.db",
            PreferencesPath: "settings.json");
    }
}
