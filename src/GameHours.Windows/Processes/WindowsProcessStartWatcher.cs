using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

internal readonly record struct WindowsProcessStartObservation(
    ProcessSnapshot Snapshot,
    DateTimeOffset OccurredAtUtc);

/// <summary>
/// Uses the Windows WMI process-start trace as the primary low-cost notification path.
/// The event callback enriches only the process that actually started; no global process
/// enumeration is performed here. Periodic reconciliation remains mandatory in the monitor
/// because Windows management events can be unavailable or missed.
/// </summary>
internal sealed class WindowsProcessStartWatcher : IDisposable
{
    private const string Scope = @"\\.\root\CIMV2";
    private const string Query = "SELECT * FROM Win32_ProcessStartTrace";

    private readonly WindowsProcessSnapshotProvider _snapshotProvider;
    private ManagementEventWatcher? _watcher;
    private int _disposing;

    public WindowsProcessStartWatcher(WindowsProcessSnapshotProvider snapshotProvider)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    public event Action<WindowsProcessStartObservation>? ProcessStarted;
    public event Action? Unavailable;

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposing) != 0, this);
        if (_watcher is not null)
        {
            return true;
        }

        ManagementEventWatcher? watcher = null;
        try
        {
            watcher = new ManagementEventWatcher(Scope, Query);
            watcher.EventArrived += HandleEventArrived;
            watcher.Stopped += HandleStopped;
            watcher.Start();
            _watcher = watcher;
            return true;
        }
        catch (Exception exception) when (
            exception is ManagementException or COMException or UnauthorizedAccessException or
            InvalidOperationException or PlatformNotSupportedException)
        {
            if (watcher is not null)
            {
                watcher.EventArrived -= HandleEventArrived;
                watcher.Stopped -= HandleStopped;
                watcher.Dispose();
            }

            return false;
        }
    }

    private void HandleEventArrived(object sender, EventArrivedEventArgs args)
    {
        try
        {
            var processId = ReadProcessId(args.NewEvent["ProcessID"]);
            if (processId is null)
            {
                return;
            }

            var parentProcessId = ReadProcessId(args.NewEvent["ParentProcessID"]);
            var processName = args.NewEvent["ProcessName"] as string;
            var occurredAtUtc = ReadOccurredAtUtc(args.NewEvent["TIME_CREATED"]);
            var snapshot = _snapshotProvider.TryGetProcess(
                processId.Value,
                parentProcessId,
                processName,
                occurredAtUtc);
            if (snapshot is null)
            {
                return;
            }

            ProcessStarted?.Invoke(new WindowsProcessStartObservation(snapshot, occurredAtUtc));
        }
        catch (Exception exception) when (
            exception is ManagementException or InvalidCastException or FormatException or
            OverflowException or COMException)
        {
            // A single malformed/transient WMI event must not terminate tracking. The periodic
            // reconciliation path will recover any process that remains alive.
        }
    }

    private void HandleStopped(object sender, StoppedEventArgs args)
    {
        if (Volatile.Read(ref _disposing) == 0)
        {
            Unavailable?.Invoke();
        }
    }

    private static int? ReadProcessId(object? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            var raw = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
            return raw is > 0 and <= int.MaxValue ? (int)raw : null;
        }
        catch (Exception exception) when (
            exception is InvalidCastException or FormatException or OverflowException)
        {
            return null;
        }
    }

    private static DateTimeOffset ReadOccurredAtUtc(object? value)
    {
        try
        {
            var raw = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            if (raw <= long.MaxValue)
            {
                return DateTimeOffset.FromFileTime((long)raw).ToUniversalTime();
            }
        }
        catch (Exception exception) when (
            exception is ArgumentOutOfRangeException or InvalidCastException or
            FormatException or OverflowException)
        {
        }

        return DateTimeOffset.UtcNow;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposing, 1) != 0)
        {
            return;
        }

        var watcher = _watcher;
        _watcher = null;
        if (watcher is null)
        {
            return;
        }

        watcher.EventArrived -= HandleEventArrived;
        watcher.Stopped -= HandleStopped;
        try
        {
            watcher.Stop();
        }
        catch (Exception exception) when (
            exception is ManagementException or COMException or InvalidOperationException)
        {
        }
        finally
        {
            watcher.Dispose();
        }
    }
}
