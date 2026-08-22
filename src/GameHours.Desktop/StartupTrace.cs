using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace GameHours.Desktop;

internal static class StartupTrace
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly object Gate = new();
    private static readonly List<TraceEntry> Entries = new();
    private static bool _enabled;
    private static string? _logPath;

    public static bool IsEnabled
    {
        get
        {
            lock (Gate)
            {
                return _enabled;
            }
        }
    }

    public static string? LogPath
    {
        get
        {
            lock (Gate)
            {
                return _logPath;
            }
        }
    }

    public static void Enable()
    {
        lock (Gate)
        {
            if (_enabled)
            {
                return;
            }

            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHours",
                "diagnostics");
            Directory.CreateDirectory(directory);
            _logPath = Path.Combine(
                directory,
                $"startup-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
            _enabled = true;
        }

        Mark("startup trace enabled");
    }

    public static void Mark(string eventName)
    {
        if (string.IsNullOrWhiteSpace(eventName))
        {
            return;
        }

        lock (Gate)
        {
            // Keep early marks even before Enable() so App construction / Velopack timing is
            // available once --startup-trace is parsed in OnStartup. The list stays tiny.
            Entries.Add(new TraceEntry(
                Clock.Elapsed.TotalMilliseconds,
                Environment.CurrentManagedThreadId,
                eventName.Trim()));
        }
    }

    public static async Task FlushAsync()
    {
        string? path;
        TraceEntry[] snapshot;
        lock (Gate)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(_logPath))
            {
                return;
            }

            path = _logPath;
            snapshot = Entries.ToArray();
        }

        var lines = BuildLines(snapshot);
        try
        {
            await File.WriteAllLinesAsync(path, lines).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must never affect startup or shutdown.
        }
    }

    public static void FlushBestEffort()
    {
        string? path;
        TraceEntry[] snapshot;
        lock (Gate)
        {
            if (!_enabled || string.IsNullOrWhiteSpace(_logPath))
            {
                return;
            }

            path = _logPath;
            snapshot = Entries.ToArray();
        }

        try
        {
            File.WriteAllLines(path, BuildLines(snapshot));
        }
        catch
        {
            // Diagnostics must never affect startup or shutdown.
        }
    }

    private static string[] BuildLines(IReadOnlyList<TraceEntry> entries)
    {
        var lines = new List<string>(entries.Count + 4)
        {
            "GameHours startup trace",
            $"Captured: {DateTimeOffset.Now:O}",
            $"Process: {Environment.ProcessId} · .NET {Environment.Version}",
            "elapsed_ms\tthread\tevent"
        };

        lines.AddRange(entries.Select(entry => string.Create(
            CultureInfo.InvariantCulture,
            $"{entry.ElapsedMilliseconds,10:0.000}\tT{entry.ThreadId:00}\t{entry.EventName}")));
        return lines.ToArray();
    }

    private sealed record TraceEntry(
        double ElapsedMilliseconds,
        int ThreadId,
        string EventName);
}
