using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

public sealed class HybridWindowsProcessMonitor : IProcessMonitor
{
    private readonly IProcessSnapshotProvider _snapshotProvider;
    private readonly TimeSpan _reconciliationInterval;

    public HybridWindowsProcessMonitor(
        IProcessSnapshotProvider snapshotProvider,
        TimeSpan? reconciliationInterval = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));
        _reconciliationInterval = reconciliationInterval ?? TimeSpan.FromSeconds(1);
        if (_reconciliationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        }
    }

    public async IAsyncEnumerable<ProcessObservation> ObserveAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateUnbounded<ProcessObservation>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var worker = RunAsync(channel.Writer, linkedCts.Token);

        try
        {
            await foreach (var observation in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return observation;
            }
        }
        finally
        {
            linkedCts.Cancel();
            try
            {
                await worker;
            }
            catch (OperationCanceledException) when (linkedCts.IsCancellationRequested)
            {
            }
        }
    }

    private async Task RunAsync(ChannelWriter<ProcessObservation> writer, CancellationToken cancellationToken)
    {
        var known = new ConcurrentDictionary<int, ProcessSnapshot>();
        var watchers = new ConcurrentDictionary<int, Process>();

        try
        {
            var initial = await _snapshotProvider.GetSnapshotAsync(cancellationToken);
            var initialAt = DateTimeOffset.UtcNow;
            foreach (var process in initial)
            {
                if (known.TryAdd(process.ProcessId, process))
                {
                    TryWatchExit(process, known, watchers, writer);
                    writer.TryWrite(ToObservation(process, initialAt, ProcessObservationType.InitialSnapshot));
                }
            }

            using var timer = new PeriodicTimer(_reconciliationInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var observedAt = DateTimeOffset.UtcNow;
                var snapshot = await _snapshotProvider.GetSnapshotAsync(cancellationToken);
                var current = snapshot.ToDictionary(process => process.ProcessId);

                foreach (var process in snapshot)
                {
                    if (known.TryGetValue(process.ProcessId, out var previous))
                    {
                        if (SameProcess(previous, process))
                        {
                            continue;
                        }

                        RemoveWatcher(process.ProcessId, watchers);
                        known.TryRemove(process.ProcessId, out _);
                        writer.TryWrite(ToObservation(previous, observedAt, ProcessObservationType.ReconciledStop));
                    }

                    if (known.TryAdd(process.ProcessId, process))
                    {
                        TryWatchExit(process, known, watchers, writer);
                        writer.TryWrite(ToObservation(process, observedAt, ProcessObservationType.ReconciledStart));
                    }
                }

                foreach (var pair in known.ToArray())
                {
                    if (current.ContainsKey(pair.Key))
                    {
                        continue;
                    }

                    if (known.TryRemove(pair.Key, out var stopped))
                    {
                        RemoveWatcher(pair.Key, watchers);
                        writer.TryWrite(ToObservation(stopped, observedAt, ProcessObservationType.ReconciledStop));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            writer.TryComplete(exception);
            return;
        }
        finally
        {
            foreach (var process in watchers.Values)
            {
                process.Dispose();
            }

            watchers.Clear();
        }

        writer.TryComplete();
    }

    private static void TryWatchExit(
        ProcessSnapshot snapshot,
        ConcurrentDictionary<int, ProcessSnapshot> known,
        ConcurrentDictionary<int, Process> watchers,
        ChannelWriter<ProcessObservation> writer)
    {
        try
        {
            var process = Process.GetProcessById(snapshot.ProcessId);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (known.TryRemove(snapshot.ProcessId, out var stopped))
                {
                    writer.TryWrite(ToObservation(
                        stopped,
                        DateTimeOffset.UtcNow,
                        ProcessObservationType.ReconciledStop));
                }

                RemoveWatcher(snapshot.ProcessId, watchers);
            };

            if (!watchers.TryAdd(snapshot.ProcessId, process))
            {
                process.Dispose();
            }
        }
        catch (ArgumentException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        catch (NotSupportedException)
        {
        }
    }

    private static void RemoveWatcher(int processId, ConcurrentDictionary<int, Process> watchers)
    {
        if (watchers.TryRemove(processId, out var process))
        {
            process.Dispose();
        }
    }

    private static bool SameProcess(ProcessSnapshot left, ProcessSnapshot right)
    {
        if (left.StartedAtUtc.HasValue && right.StartedAtUtc.HasValue)
        {
            return left.StartedAtUtc.Value == right.StartedAtUtc.Value;
        }

        return string.Equals(left.ExecutablePath, right.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            && string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase);
    }

    private static ProcessObservation ToObservation(
        ProcessSnapshot process,
        DateTimeOffset occurredAtUtc,
        ProcessObservationType type) =>
        new(
            process.ProcessId,
            process.ProcessName,
            process.ExecutablePath,
            occurredAtUtc.ToUniversalTime(),
            type);
}
