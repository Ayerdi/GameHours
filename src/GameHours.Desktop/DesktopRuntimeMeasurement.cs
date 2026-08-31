using System.Diagnostics;

namespace GameHours.Desktop;

internal sealed record DesktopRuntimeMeasurement(
    TimeSpan Duration,
    double? CpuPercent,
    long? AveragePrivateMemoryBytes,
    long? PeakPrivateMemoryBytes,
    long? AverageWorkingSetBytes,
    long? PeakWorkingSetBytes,
    double? AverageThreadCount,
    int? PeakThreadCount,
    long? AverageManagedHeapBytes,
    long? PeakManagedHeapBytes,
    double? ManagedAllocationRateBytesPerSecond,
    long? PeakGcCommittedBytes,
    long? PeakGcFragmentedBytes,
    double? GcPausePercent,
    int? Gen0CollectionDelta,
    int? Gen1CollectionDelta,
    int? Gen2CollectionDelta,
    long? ReconciliationDelta);

internal static class DesktopRuntimeMeasurementSampler
{
    public static async Task<DesktopRuntimeMeasurement> MeasureAsync(
        Func<DesktopRuntimeDiagnostics> capture,
        TimeSpan duration,
        TimeSpan sampleInterval,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(capture);
        if (duration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(duration));
        if (sampleInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(sampleInterval));

        var samples = new List<DesktopRuntimeDiagnostics> { capture() };
        var started = Stopwatch.GetTimestamp();

        while (Stopwatch.GetElapsedTime(started) < duration)
        {
            var remaining = duration - Stopwatch.GetElapsedTime(started);
            await Task.Delay(remaining < sampleInterval ? remaining : sampleInterval, cancellationToken);
            samples.Add(capture());
        }

        return Calculate(samples, Stopwatch.GetElapsedTime(started), Environment.ProcessorCount);
    }

    internal static DesktopRuntimeMeasurement Calculate(
        IReadOnlyList<DesktopRuntimeDiagnostics> samples,
        TimeSpan elapsed,
        int processorCount)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 2) throw new ArgumentException("At least two runtime snapshots are required.", nameof(samples));
        if (elapsed <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        if (processorCount <= 0) throw new ArgumentOutOfRangeException(nameof(processorCount));

        var first = samples[0];
        var last = samples[^1];
        double? cpuPercent = null;
        if (last.ProcessCpuTime >= first.ProcessCpuTime)
        {
            var cpuSeconds = (last.ProcessCpuTime - first.ProcessCpuTime).TotalSeconds;
            var capacitySeconds = elapsed.TotalSeconds * processorCount;
            cpuPercent = Math.Clamp(cpuSeconds / capacitySeconds * 100d, 0d, 100d);
        }

        var privateMemory = samples.Select(item => item.PrivateMemoryBytes).Where(value => value > 0).ToArray();
        var workingSet = samples.Select(item => item.WorkingSetBytes).Where(value => value > 0).ToArray();
        var threads = samples.Select(item => item.ThreadCount).Where(value => value > 0).ToArray();
        var managedHeap = samples.Select(item => item.ManagedHeapBytes).Where(value => value > 0).ToArray();
        var gcCommitted = samples.Select(item => item.GcCommittedBytes).Where(value => value > 0).ToArray();
        var gcFragmented = samples.Select(item => item.GcFragmentedBytes).Where(value => value >= 0).ToArray();

        double? managedAllocationRate = null;
        if (last.TotalAllocatedBytes >= first.TotalAllocatedBytes)
        {
            managedAllocationRate = (last.TotalAllocatedBytes - first.TotalAllocatedBytes) / elapsed.TotalSeconds;
        }

        double? gcPausePercent = null;
        if (last.GcTotalPauseDuration >= first.GcTotalPauseDuration)
        {
            var pauseSeconds = (last.GcTotalPauseDuration - first.GcTotalPauseDuration).TotalSeconds;
            gcPausePercent = Math.Clamp(pauseSeconds / elapsed.TotalSeconds * 100d, 0d, 100d);
        }

        long? reconciliationDelta = null;
        if (first.ProcessMonitor.IsRunning &&
            last.ProcessMonitor.IsRunning &&
            last.ProcessMonitor.FullReconciliations >= first.ProcessMonitor.FullReconciliations)
        {
            reconciliationDelta = last.ProcessMonitor.FullReconciliations - first.ProcessMonitor.FullReconciliations;
        }

        return new DesktopRuntimeMeasurement(
            elapsed,
            cpuPercent,
            Average(privateMemory),
            privateMemory.Length == 0 ? null : privateMemory.Max(),
            Average(workingSet),
            workingSet.Length == 0 ? null : workingSet.Max(),
            threads.Length == 0 ? null : threads.Average(),
            threads.Length == 0 ? null : threads.Max(),
            Average(managedHeap),
            managedHeap.Length == 0 ? null : managedHeap.Max(),
            managedAllocationRate,
            gcCommitted.Length == 0 ? null : gcCommitted.Max(),
            gcFragmented.Length == 0 ? null : gcFragmented.Max(),
            gcPausePercent,
            CounterDelta(first.Gen0CollectionCount, last.Gen0CollectionCount),
            CounterDelta(first.Gen1CollectionCount, last.Gen1CollectionCount),
            CounterDelta(first.Gen2CollectionCount, last.Gen2CollectionCount),
            reconciliationDelta);
    }

    private static long? Average(long[] values) =>
        values.Length == 0 ? null : checked((long)Math.Round(values.Average()));

    private static int? CounterDelta(int before, int after) => after >= before ? after - before : null;
}
