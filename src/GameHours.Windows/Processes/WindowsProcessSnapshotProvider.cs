using System.Diagnostics;
using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

public sealed class WindowsProcessSnapshotProvider : IProcessSnapshotProvider
{
    public Task<IReadOnlyList<ProcessSnapshot>> GetSnapshotAsync(
        CancellationToken cancellationToken = default)
    {
        return Task.Run<IReadOnlyList<ProcessSnapshot>>(() =>
        {
            var snapshots = new List<ProcessSnapshot>();

            foreach (var process in Process.GetProcesses())
            {
                cancellationToken.ThrowIfCancellationRequested();
                using (process)
                {
                    var name = SafeGet(() => process.ProcessName) ?? $"pid-{process.Id}";
                    var path = SafeGet(() => process.MainModule?.FileName);
                    var startedAtUtc = SafeGetDate(() => process.StartTime.ToUniversalTime());

                    snapshots.Add(new ProcessSnapshot(
                        process.Id,
                        name,
                        path,
                        startedAtUtc));
                }
            }

            return snapshots;
        }, cancellationToken);
    }

    private static string? SafeGet(Func<string?> getter)
    {
        try
        {
            return getter();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? SafeGetDate(Func<DateTime> getter)
    {
        try
        {
            return new DateTimeOffset(getter(), TimeSpan.Zero);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}
