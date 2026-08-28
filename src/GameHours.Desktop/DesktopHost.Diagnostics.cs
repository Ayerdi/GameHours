using System.Diagnostics;
using GameHours.Windows.Processes;

namespace GameHours.Desktop;

public sealed record DesktopRuntimeDiagnostics(
    bool IsTracking,
    string StatusText,
    string? ActiveGameTitle,
    DesktopPreferences Preferences,
    int? AppliedAfkTimeoutMinutes,
    WindowsProcessMonitorDiagnostics ProcessMonitor,
    TimeSpan ProcessCpuTime,
    long PrivateMemoryBytes,
    long WorkingSetBytes,
    int ThreadCount,
    long ManagedHeapBytes,
    long TotalAllocatedBytes,
    int Gen0CollectionCount,
    int Gen1CollectionCount,
    int Gen2CollectionCount,
    long GcCommittedBytes,
    long GcFragmentedBytes,
    TimeSpan GcTotalPauseDuration,
    string DatabasePath,
    string PreferencesPath);

public sealed partial class DesktopHost
{
    public DesktopRuntimeDiagnostics GetRuntimeDiagnostics()
    {
        var monitor = _monitor?.GetDiagnostics() ?? new WindowsProcessMonitorDiagnostics(
            IsRunning: false,
            EventDrivenActive: false,
            DegradedFallback: false,
            ProcessStartEvents: 0,
            FullReconciliations: 0,
            LastReconciliationAtUtc: null);
        var trackerRunning = IsTrackerRunning;

        var cpu = TimeSpan.Zero;
        long privateMemory = 0;
        long workingSet = 0;
        var threadCount = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            cpu = process.TotalProcessorTime;
            privateMemory = process.PrivateMemorySize64;
            workingSet = process.WorkingSet64;
            threadCount = process.Threads.Count;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or NotSupportedException or
            System.ComponentModel.Win32Exception)
        {
            // Diagnostics are observational only. A platform/process-query failure must never
            // affect tracking or force another background measurement loop.
        }

        // These GC counters are cheap, process-local observations. They do not force a
        // collection and intentionally avoid the expensive precise-allocation query.
        var managedHeap = GC.GetTotalMemory(forceFullCollection: false);
        var totalAllocated = GC.GetTotalAllocatedBytes(precise: false);
        var gen0Collections = GC.CollectionCount(0);
        var gen1Collections = GC.CollectionCount(1);
        var gen2Collections = GC.CollectionCount(2);
        var gcMemoryInfo = GC.GetGCMemoryInfo();
        var gcCommitted = gcMemoryInfo.TotalCommittedBytes;
        var gcFragmented = gcMemoryInfo.FragmentedBytes;
        var gcPauseDuration = GC.GetTotalPauseDuration();

        return new DesktopRuntimeDiagnostics(
            trackerRunning,
            _currentStatus.StatusText,
            _currentStatus.ActiveGameTitle,
            _preferences,
            trackerRunning ? Volatile.Read(ref _appliedAfkTimeoutMinutes) : null,
            monitor,
            cpu,
            privateMemory,
            workingSet,
            threadCount,
            managedHeap,
            totalAllocated,
            gen0Collections,
            gen1Collections,
            gen2Collections,
            gcCommitted,
            gcFragmented,
            gcPauseDuration,
            DatabasePath,
            PreferencesPath);
    }
}
