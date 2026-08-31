using System.Threading.Channels;

namespace GameHours.Windows.IO;

public enum TargetFileWakeReason
{
    Changed = 1,
    Fallback = 2,
    WatcherFaulted = 3
}

/// <summary>
/// Watches one exact local file and coalesces duplicate filesystem notifications into a single
/// wake-up. It deliberately keeps FileSystemWatcher's default buffer size and narrow filters;
/// callers keep a low-frequency fallback read because filesystem notifications are advisory.
/// </summary>
public sealed class TargetFileChangeWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Channel<byte> _signals;
    private int _faulted;
    private int _disposed;

    private TargetFileChangeWatcher(string fullPath, FileSystemWatcher watcher)
    {
        FullPath = fullPath;
        _watcher = watcher;
        _signals = Channel.CreateBounded<byte>(
            new BoundedChannelOptions(1)
            {
                SingleReader = true,
                SingleWriter = false,
                FullMode = BoundedChannelFullMode.DropWrite,
                AllowSynchronousContinuations = false
            });

        _watcher.Changed += HandleChanged;
        _watcher.Created += HandleChanged;
        _watcher.Deleted += HandleChanged;
        _watcher.Renamed += HandleRenamed;
        _watcher.Error += HandleError;
        _watcher.EnableRaisingEvents = true;
    }

    public string FullPath { get; }

    public static TargetFileChangeWatcher? TryCreate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        FileSystemWatcher? watcher = null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(directory) ||
                string.IsNullOrWhiteSpace(fileName) ||
                !Directory.Exists(directory))
            {
                return null;
            }

            watcher = new FileSystemWatcher(directory, fileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime
            };

            return new TargetFileChangeWatcher(fullPath, watcher);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            PathTooLongException or NotSupportedException or PlatformNotSupportedException)
        {
            watcher?.Dispose();
            return null;
        }
    }

    public async Task<TargetFileWakeReason> WaitAsync(
        TimeSpan fallbackInterval,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (fallbackInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(fallbackInterval));
        }

        if (Interlocked.Exchange(ref _faulted, 0) != 0)
        {
            _signals.Reader.TryRead(out _);
            return TargetFileWakeReason.WatcherFaulted;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(fallbackInterval);
        try
        {
            _ = await _signals.Reader.ReadAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TargetFileWakeReason.Fallback;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return Interlocked.Exchange(ref _faulted, 0) != 0
            ? TargetFileWakeReason.WatcherFaulted
            : TargetFileWakeReason.Changed;
    }

    private void HandleChanged(object sender, FileSystemEventArgs args) => Signal();

    private void HandleRenamed(object sender, RenamedEventArgs args) => Signal();

    private void HandleError(object sender, ErrorEventArgs args)
    {
        Interlocked.Exchange(ref _faulted, 1);
        Signal();
    }

    private void Signal()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            _signals.Writer.TryWrite(0);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            _watcher.EnableRaisingEvents = false;
        }
        catch
        {
        }

        _watcher.Changed -= HandleChanged;
        _watcher.Created -= HandleChanged;
        _watcher.Deleted -= HandleChanged;
        _watcher.Renamed -= HandleRenamed;
        _watcher.Error -= HandleError;
        _watcher.Dispose();
        _signals.Writer.TryComplete();
    }
}
