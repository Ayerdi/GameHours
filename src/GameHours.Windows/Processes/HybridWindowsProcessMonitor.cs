using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using GameHours.Core.Abstractions;
using GameHours.Core.Monitoring;

namespace GameHours.Windows.Processes;

public sealed record WindowsProcessMonitorDiagnostics(
    bool IsRunning,
    bool EventDrivenActive,
    bool DegradedFallback,
    long ProcessStartEvents,
    long FullReconciliations,
    DateTimeOffset? LastReconciliationAtUtc);

public sealed class HybridWindowsProcessMonitor : IProcessMonitor
{
    private static readonly TimeSpan EventDrivenReconciliationInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SleepSampleInterval = TimeSpan.FromSeconds(1);

    private readonly record struct MonitorSignal(
        WindowsProcessStartObservation? ProcessStart,
        bool EventSourceUnavailable)
    {
        public static MonitorSignal Started(WindowsProcessStartObservation observation) =>
            new(observation, false);

        public static MonitorSignal SourceUnavailable() => new(null, true);
    }

    private readonly IProcessSnapshotProvider _snapshotProvider;
    private readonly TimeSpan _degradedReconciliationInterval;
    private readonly WindowsSystemUptimeSampleProvider _uptimeSamples = new();
    private int _isRunning;
    private int _eventDrivenActive;
    private int _degradedFallback;
    private long _processStartEvents;
    private long _fullReconciliations;
    private long _lastReconciliationUtcTicks;

    public HybridWindowsProcessMonitor(
        IProcessSnapshotProvider snapshotProvider,
        TimeSpan? reconciliationInterval = null)
    {
        _snapshotProvider = snapshotProvider ?? throw new ArgumentNullException(nameof(snapshotProvider));

        // The public constructor historically accepted the one-second reconciliation interval.
        // Keep that value as the degraded fallback so machines without WMI retain the old
        // behaviour. When process-start events are available, full snapshots are only a safety
        // reconciliation every five seconds.
        _degradedReconciliationInterval = reconciliationInterval ?? TimeSpan.FromSeconds(1);
        if (_degradedReconciliationInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        }
    }

    public WindowsProcessMonitorDiagnostics GetDiagnostics()
    {
        var ticks = Interlocked.Read(ref _lastReconciliationUtcTicks);
        return new WindowsProcessMonitorDiagnostics(
            Volatile.Read(ref _isRunning) != 0,
            Volatile.Read(ref _eventDrivenActive) != 0,
            Volatile.Read(ref _degradedFallback) != 0,
            Interlocked.Read(ref _processStartEvents),
            Interlocked.Read(ref _fullReconciliations),
            ticks > 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null);
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
        Interlocked.Exchange(ref _isRunning, 1);
        var known = new ConcurrentDictionary<int, ProcessSnapshot>();
        var watchers = new ConcurrentDictionary<int, Process>();
        var sleepDetector = new SystemSleepGapDetector();
        var signals = Channel.CreateUnbounded<MonitorSignal>(
            new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false,
                AllowSynchronousContinuations = false
            });

        WindowsProcessStartWatcher? processStartWatcher = null;
        var eventDriven = false;

        try
        {
            if (_snapshotProvider is WindowsProcessSnapshotProvider windowsSnapshotProvider)
            {
                processStartWatcher = new WindowsProcessStartWatcher(windowsSnapshotProvider);
                processStartWatcher.ProcessStarted += observation =>
                    signals.Writer.TryWrite(MonitorSignal.Started(observation));
                processStartWatcher.Unavailable += () =>
                    signals.Writer.TryWrite(MonitorSignal.SourceUnavailable());
                eventDriven = processStartWatcher.TryStart();
                if (!eventDriven)
                {
                    processStartWatcher.Dispose();
                    processStartWatcher = null;
                }
            }

            Interlocked.Exchange(ref _eventDrivenActive, eventDriven ? 1 : 0);
            Interlocked.Exchange(ref _degradedFallback, eventDriven ? 0 : 1);

            if (_uptimeSamples.TryGetSample(out var baselineSample) && baselineSample is not null)
            {
                sleepDetector.Observe(baselineSample);
            }

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

            var reconciliationInterval = eventDriven
                ? EventDrivenReconciliationInterval
                : _degradedReconciliationInterval;
            var lastReconciliationAt = initialAt.ToUniversalTime();
            Interlocked.Exchange(ref _lastReconciliationUtcTicks, lastReconciliationAt.UtcTicks);

            Task<bool> signalReadyTask = signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
            Task sleepDelayTask = Task.Delay(SleepSampleInterval, cancellationToken);
            Task reconciliationDelayTask = Task.Delay(reconciliationInterval, cancellationToken);

            while (!cancellationToken.IsCancellationRequested)
            {
                _ = await Task.WhenAny(signalReadyTask, sleepDelayTask, reconciliationDelayTask);

                if (signalReadyTask.IsCompleted)
                {
                    if (!await signalReadyTask)
                    {
                        break;
                    }

                    while (signals.Reader.TryRead(out var signal))
                    {
                        if (signal.ProcessStart is { } processStart)
                        {
                            Interlocked.Increment(ref _processStartEvents);
                            HandleProcessStart(processStart, known, watchers, writer);
                        }

                        if (signal.EventSourceUnavailable && eventDriven)
                        {
                            eventDriven = false;
                            Interlocked.Exchange(ref _eventDrivenActive, 0);
                            Interlocked.Exchange(ref _degradedFallback, 1);
                            reconciliationInterval = _degradedReconciliationInterval;
                            processStartWatcher?.Dispose();
                            processStartWatcher = null;

                            // Reconcile immediately, then use the old one-second cadence. This
                            // prevents a WMI failure from reducing detection reliability.
                            reconciliationDelayTask = Task.CompletedTask;
                        }
                    }

                    signalReadyTask = signals.Reader.WaitToReadAsync(cancellationToken).AsTask();
                }

                if (sleepDelayTask.IsCompleted)
                {
                    await sleepDelayTask;
                    if (_uptimeSamples.TryGetSample(out var uptimeSample) && uptimeSample is not null)
                    {
                        var sleepGap = sleepDetector.Observe(uptimeSample);
                        if (sleepGap is not null)
                        {
                            HandleSleepGap(sleepGap, known, watchers, writer);
                            await ReconcileAsync(
                                known,
                                watchers,
                                writer,
                                sleepGap.SuspendedAtUtc,
                                uptimeSample.ObservedAtUtc,
                                cancellationToken);
                            lastReconciliationAt = uptimeSample.ObservedAtUtc.ToUniversalTime();
                            RecordReconciliation(lastReconciliationAt);
                            reconciliationDelayTask = Task.Delay(reconciliationInterval, cancellationToken);
                        }
                    }

                    sleepDelayTask = Task.Delay(SleepSampleInterval, cancellationToken);
                }

                if (reconciliationDelayTask.IsCompleted)
                {
                    await reconciliationDelayTask;
                    var observedAt = DateTimeOffset.UtcNow;
                    await ReconcileAsync(
                        known,
                        watchers,
                        writer,
                        lastReconciliationAt,
                        observedAt,
                        cancellationToken);
                    lastReconciliationAt = observedAt.ToUniversalTime();
                    RecordReconciliation(lastReconciliationAt);
                    reconciliationDelayTask = Task.Delay(reconciliationInterval, cancellationToken);
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
            Interlocked.Exchange(ref _isRunning, 0);
            Interlocked.Exchange(ref _eventDrivenActive, 0);
            Interlocked.Exchange(ref _degradedFallback, 0);
            processStartWatcher?.Dispose();
            signals.Writer.TryComplete();

            foreach (var process in watchers.Values)
            {
                process.Dispose();
            }

            watchers.Clear();
        }

        writer.TryComplete();
    }

    private void RecordReconciliation(DateTimeOffset observedAtUtc)
    {
        Interlocked.Increment(ref _fullReconciliations);
        Interlocked.Exchange(ref _lastReconciliationUtcTicks, observedAtUtc.ToUniversalTime().UtcTicks);
    }

    private async Task ReconcileAsync(
        ConcurrentDictionary<int, ProcessSnapshot> known,
        ConcurrentDictionary<int, Process> watchers,
        ChannelWriter<ProcessObservation> writer,
        DateTimeOffset previousReconciliationAtUtc,
        DateTimeOffset observedAtUtc,
        CancellationToken cancellationToken)
    {
        var snapshot = await _snapshotProvider.GetSnapshotAsync(cancellationToken);
        var current = snapshot.ToDictionary(process => process.ProcessId);

        foreach (var process in snapshot)
        {
            if (known.TryGetValue(process.ProcessId, out var previous))
            {
                if (SameProcess(previous, process))
                {
                    if (NeedsEnrichment(previous, process) &&
                        known.TryUpdate(process.ProcessId, process, previous))
                    {
                        writer.TryWrite(ToObservation(
                            process,
                            observedAtUtc,
                            ProcessObservationType.ReconciledStart));
                    }

                    continue;
                }

                RemoveWatcher(process.ProcessId, watchers);
                known.TryRemove(process.ProcessId, out _);
                writer.TryWrite(ToObservation(
                    previous,
                    observedAtUtc,
                    ProcessObservationType.ReconciledStop));
            }

            if (known.TryAdd(process.ProcessId, process))
            {
                TryWatchExit(process, known, watchers, writer);
                writer.TryWrite(ToObservation(
                    process,
                    GetReconciledStartAt(process, previousReconciliationAtUtc, observedAtUtc),
                    ProcessObservationType.ReconciledStart));
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
                writer.TryWrite(ToObservation(
                    stopped,
                    observedAtUtc,
                    ProcessObservationType.ReconciledStop));
            }
        }
    }

    private static void HandleProcessStart(
        WindowsProcessStartObservation observation,
        ConcurrentDictionary<int, ProcessSnapshot> known,
        ConcurrentDictionary<int, Process> watchers,
        ChannelWriter<ProcessObservation> writer)
    {
        var process = observation.Snapshot;
        if (known.TryGetValue(process.ProcessId, out var previous))
        {
            if (SameProcess(previous, process))
            {
                if (NeedsEnrichment(previous, process) &&
                    known.TryUpdate(process.ProcessId, process, previous))
                {
                    writer.TryWrite(ToObservation(
                        process,
                        observation.OccurredAtUtc,
                        ProcessObservationType.Started));
                }

                return;
            }

            RemoveWatcher(process.ProcessId, watchers);
            known.TryRemove(process.ProcessId, out _);
            writer.TryWrite(ToObservation(
                previous,
                observation.OccurredAtUtc,
                ProcessObservationType.ReconciledStop));
        }

        if (known.TryAdd(process.ProcessId, process))
        {
            TryWatchExit(process, known, watchers, writer);
            writer.TryWrite(ToObservation(
                process,
                observation.OccurredAtUtc,
                ProcessObservationType.Started));
        }
    }

    private static void HandleSleepGap(
        SystemSleepGap sleepGap,
        ConcurrentDictionary<int, ProcessSnapshot> known,
        ConcurrentDictionary<int, Process> watchers,
        ChannelWriter<ProcessObservation> writer)
    {
        // Sleep detection stays on a cheap one-second uptime sample, independently from the
        // slower full-process reconciliation. A surviving game therefore never absorbs the
        // suspended wall-clock interval just because global process scans became less frequent.
        foreach (var pair in known.ToArray())
        {
            if (!known.TryRemove(pair.Key, out var suspended))
            {
                continue;
            }

            RemoveWatcher(pair.Key, watchers);
            writer.TryWrite(ToObservation(
                suspended,
                sleepGap.SuspendedAtUtc,
                ProcessObservationType.ReconciledStop));
        }
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
                return;
            }

            if (process.HasExited && known.TryRemove(snapshot.ProcessId, out var stopped))
            {
                writer.TryWrite(ToObservation(
                    stopped,
                    DateTimeOffset.UtcNow,
                    ProcessObservationType.ReconciledStop));
                RemoveWatcher(snapshot.ProcessId, watchers);
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

    private static bool NeedsEnrichment(ProcessSnapshot previous, ProcessSnapshot current) =>
        string.IsNullOrWhiteSpace(previous.ExecutablePath) &&
        !string.IsNullOrWhiteSpace(current.ExecutablePath);

    internal static DateTimeOffset GetReconciledStartAt(
        ProcessSnapshot process,
        DateTimeOffset previousReconciliationAtUtc,
        DateTimeOffset observedAtUtc)
    {
        var upperBound = observedAtUtc.ToUniversalTime();
        var lowerBound = previousReconciliationAtUtc.ToUniversalTime();
        if (lowerBound > upperBound)
        {
            lowerBound = upperBound;
        }

        if (process.StartedAtUtc is not { } startedAtUtc)
        {
            return upperBound;
        }

        var startedAt = startedAtUtc.ToUniversalTime();
        if (startedAt < lowerBound)
        {
            return lowerBound;
        }

        return startedAt > upperBound ? upperBound : startedAt;
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
