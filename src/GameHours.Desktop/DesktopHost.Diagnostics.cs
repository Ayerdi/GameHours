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
    long WorkingSetBytes,
    int ThreadCount,
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
        long workingSet = 0;
        var threadCount = 0;
        try
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            cpu = process.TotalProcessorTime;
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

        return new DesktopRuntimeDiagnostics(
            trackerRunning,
            _currentStatus.StatusText,
            _currentStatus.ActiveGameTitle,
            _preferences,
            trackerRunning ? Volatile.Read(ref _appliedAfkTimeoutMinutes) : null,
            monitor,
            cpu,
            workingSet,
            threadCount,
            DatabasePath,
            PreferencesPath);
    }
}
