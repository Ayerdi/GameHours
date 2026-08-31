using System.Diagnostics;
using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Processes;

public sealed class WindowsProcessSnapshotProvider : IProcessSnapshotProvider
{
    private readonly IRecentProcessIdentityHistory _history;
    private readonly Func<DateTimeOffset> _utcNow;

    public WindowsProcessSnapshotProvider(
        IRecentProcessIdentityHistory? history = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _history = history ?? WindowsProcessRelationshipHistory.Shared;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public Task<IReadOnlyList<ProcessSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ProcessSnapshot>>(() => Capture(cancellationToken), cancellationToken);

    internal ProcessSnapshot? TryGetProcess(
        int processId,
        int? parentProcessId = null,
        string? processName = null,
        DateTimeOffset? observedAtUtc = null)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var snapshot = CaptureProcess(process, parentProcessId, processName);
            _history.Observe(snapshot, (observedAtUtc ?? _utcNow()).ToUniversalTime());
            return snapshot;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private IReadOnlyList<ProcessSnapshot> Capture(CancellationToken cancellationToken)
    {
        var parents = WindowsParentProcessSnapshot.Capture();
        var observedAt = _utcNow().ToUniversalTime();
        var snapshots = new List<ProcessSnapshot>();

        foreach (var process in Process.GetProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (process)
            {
                var snapshot = CaptureProcess(
                    process,
                    parents.TryGetValue(process.Id, out var parentId) ? parentId : null);
                snapshots.Add(snapshot);
                _history.Observe(snapshot, observedAt);
            }
        }

        return snapshots;
    }

    private static ProcessSnapshot CaptureProcess(
        Process process,
        int? parentProcessId,
        string? processName = null)
    {
        var processId = process.Id;
        return new ProcessSnapshot(
            processId,
            SafeGet(() => process.ProcessName) ?? processName ?? $"pid-{processId}",
            SafeGet(() => process.MainModule?.FileName),
            SafeGetDate(() => process.StartTime.ToUniversalTime()),
            parentProcessId);
    }

    private static T? SafeGet<T>(Func<T?> getter) where T : class
    {
        try { return getter(); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }

    private static DateTimeOffset? SafeGetDate(Func<DateTime> getter)
    {
        try { return new DateTimeOffset(getter(), TimeSpan.Zero); }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
}
