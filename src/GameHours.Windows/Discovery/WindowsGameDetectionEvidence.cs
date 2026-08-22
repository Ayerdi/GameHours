using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using Microsoft.Win32;

namespace GameHours.Windows.Discovery;

public interface IWindowsGameConfigStore
{
    bool ContainsExecutable(string executablePath);
}

public sealed class WindowsGameConfigStore : IWindowsGameConfigStore
{
    private const string ChildrenKey = @"System\GameConfigStore\Children";
    private readonly object _gate = new();
    private readonly TimeSpan _cacheDuration;
    private DateTimeOffset _loadedAtUtc = DateTimeOffset.MinValue;
    private HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);

    public WindowsGameConfigStore(TimeSpan? cacheDuration = null)
    {
        _cacheDuration = cacheDuration ?? TimeSpan.FromMinutes(2);
        if (_cacheDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(cacheDuration));
        }
    }

    public bool ContainsExecutable(string executablePath)
    {
        var normalized = NormalizePath(executablePath);
        if (normalized is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _loadedAtUtc >= _cacheDuration)
            {
                _paths = ReadPaths();
                _loadedAtUtc = DateTimeOffset.UtcNow;
            }

            return _paths.Contains(normalized);
        }
    }

    private static HashSet<string> ReadPaths()
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey, writable: false);
            if (children is null)
            {
                return results;
            }

            foreach (var childName in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(childName, writable: false);
                if (child?.GetValue("MatchedExeFullPath") is not string value)
                {
                    continue;
                }

                var normalized = NormalizePath(value);
                if (normalized is not null)
                {
                    results.Add(normalized);
                }
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            // GameConfigStore is supporting evidence only. Registry access must never block tracking.
        }

        return results;
    }

    private static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(value.Trim().Trim('"'));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}

public static class WindowsExecutableRoleClassifier
{
    public static ExecutableRole Classify(string executablePath)
    {
        var fileName = Path.GetFileName(executablePath);
        var name = Path.GetFileNameWithoutExtension(fileName);

        if (Matches(fileName, "CrashReportClient.exe", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "crashpad_handler.exe"))
        {
            return ExecutableRole.CrashHandler;
        }

        if (fileName.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) ||
            Matches(fileName, "BEService.exe", "BEService_x64.exe", "vgtray.exe"))
        {
            return ExecutableRole.AntiCheat;
        }

        if (Matches(fileName, "steamwebhelper.exe", "EpicWebHelper.exe", "CefSharp.BrowserSubprocess.exe", "QtWebEngineProcess.exe"))
        {
            return ExecutableRole.Helper;
        }

        if (Matches(fileName, "steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "GalaxyClientService.exe", "EADesktop.exe", "upc.exe", "UbisoftConnect.exe", "Battle.net.exe") ||
            name.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase) ||
            Matches(fileName, "start_protected_game.exe"))
        {
            return ExecutableRole.Launcher;
        }

        if (name.EndsWith("Updater", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Update", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Patcher", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("Unins", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableRole.Updater;
        }

        if (fileName.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase))
        {
            return ExecutableRole.PrimaryGame;
        }

        var directory = Path.GetDirectoryName(executablePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            var baseName = Path.GetFileNameWithoutExtension(executablePath);
            if (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) ||
                Directory.Exists(Path.Combine(directory, $"{baseName}_Data")))
            {
                return ExecutableRole.PrimaryGame;
            }
        }

        return ExecutableRole.Unknown;
    }

    private static bool Matches(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Equals(candidate, StringComparison.OrdinalIgnoreCase));
}

public interface IWindowsProcessParentProvider
{
    int? TryGetParentProcessId(int processId);
    string? TryGetExecutablePath(int processId);
}

public sealed class WindowsProcessParentProvider : IWindowsProcessParentProvider
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    public int? TryGetParentProcessId(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>(),
                ExecutableFile = string.Empty
            };

            if (!Process32First(snapshot, ref entry))
            {
                return null;
            }

            do
            {
                if (entry.ProcessId != (uint)processId)
                {
                    continue;
                }

                if (entry.ParentProcessId == 0 || entry.ParentProcessId == entry.ProcessId)
                {
                    return null;
                }

                try
                {
                    return checked((int)entry.ParentProcessId);
                }
                catch (OverflowException)
                {
                    return null;
                }
            }
            while (Process32Next(snapshot, ref entry));

            return null;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    public string? TryGetExecutablePath(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            InvalidOperationException or
            System.ComponentModel.Win32Exception or
            NotSupportedException)
        {
            return null;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32FirstW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "Process32NextW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}

public interface IRecentProcessIdentityHistory
{
    void Observe(ProcessSnapshot process, DateTimeOffset observedAtUtc);

    string? TryGetExecutablePath(
        int processId,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? childStartedAtUtc = null);
}

public sealed class RecentProcessIdentityHistory : IRecentProcessIdentityHistory
{
    private readonly object _gate = new();
    private readonly TimeSpan _retention;
    private readonly Dictionary<int, Entry> _entries = new();

    public RecentProcessIdentityHistory(TimeSpan? retention = null)
    {
        _retention = retention ?? TimeSpan.FromSeconds(30);
        if (_retention <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retention));
        }
    }

    public void Observe(ProcessSnapshot process, DateTimeOffset observedAtUtc)
    {
        var path = NormalizePath(process.ExecutablePath);
        if (process.ProcessId <= 0 || path is null)
        {
            return;
        }

        lock (_gate)
        {
            Prune(observedAtUtc);
            _entries[process.ProcessId] = new Entry(
                path,
                process.StartedAtUtc,
                observedAtUtc.ToUniversalTime());
        }
    }

    public string? TryGetExecutablePath(
        int processId,
        DateTimeOffset observedAtUtc,
        DateTimeOffset? childStartedAtUtc = null)
    {
        if (processId <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            Prune(observedAtUtc);
            if (!_entries.TryGetValue(processId, out var entry))
            {
                return null;
            }

            if (childStartedAtUtc is DateTimeOffset childStarted &&
                entry.StartedAtUtc is DateTimeOffset parentStarted &&
                parentStarted > childStarted.AddSeconds(1))
            {
                // The PID has been reused by a process that started after the child. It cannot
                // be the parent recorded in the child's PROCESSENTRY32 relationship.
                return null;
            }

            return entry.ExecutablePath;
        }
    }

    private void Prune(DateTimeOffset observedAtUtc)
    {
        var now = observedAtUtc.ToUniversalTime();
        foreach (var pair in _entries.ToArray())
        {
            var age = now - pair.Value.LastSeenAtUtc;
            if (age > _retention)
            {
                _entries.Remove(pair.Key);
            }
        }
    }

    private static string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath.Trim().Trim('"'));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private sealed record Entry(
        string ExecutablePath,
        DateTimeOffset? StartedAtUtc,
        DateTimeOffset LastSeenAtUtc);
}

public sealed record WindowsProcessEvidence(
    ExecutableRole Role,
    IReadOnlyList<GameDetectionEvidence> Evidence,
    bool IsInGameConfigStore,
    bool HasGraphicsRuntime,
    bool HasVisibleWindow,
    bool IsForegroundWindow);

public sealed class WindowsProcessEvidenceCollector
{
    private static readonly HashSet<string> GraphicsModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "d3d9.dll",
        "d3d10.dll",
        "d3d11.dll",
        "d3d12.dll",
        "vulkan-1.dll",
        "opengl32.dll"
    };

    private readonly IWindowsGameConfigStore _gameConfigStore;
    private readonly IExecutableRoleOverrideStore _roleOverrides;
    private readonly IWindowsProcessParentProvider _parentProvider;
    private readonly IRecentProcessIdentityHistory _relationshipHistory;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly bool _inspectLiveProcess;

    public WindowsProcessEvidenceCollector(
        IWindowsGameConfigStore? gameConfigStore = null,
        bool inspectLiveProcess = true,
        IExecutableRoleOverrideStore? roleOverrides = null,
        IWindowsProcessParentProvider? parentProvider = null,
        IRecentProcessIdentityHistory? relationshipHistory = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _gameConfigStore = gameConfigStore ?? new WindowsGameConfigStore();
        _roleOverrides = roleOverrides ?? new LocalExecutableRoleOverrideStore();
        _parentProvider = parentProvider ?? new WindowsProcessParentProvider();
        _relationshipHistory = relationshipHistory ?? new RecentProcessIdentityHistory();
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _inspectLiveProcess = inspectLiveProcess;
    }

    public WindowsProcessEvidence Collect(ProcessSnapshot process)
    {
        if (string.IsNullOrWhiteSpace(process.ExecutablePath))
        {
            return new WindowsProcessEvidence(
                ExecutableRole.Unknown,
                Array.Empty<GameDetectionEvidence>(),
                false,
                false,
                false,
                false);
        }

        var path = Path.GetFullPath(process.ExecutablePath);
        var observedAtUtc = _utcNow().ToUniversalTime();
        var normalizedProcess = process with { ExecutablePath = path };
        _relationshipHistory.Observe(normalizedProcess, observedAtUtc);

        var evidence = new List<GameDetectionEvidence>();
        var hasUserOverride = _roleOverrides.TryGetRole(path, out var overriddenRole);
        var role = hasUserOverride
            ? overriddenRole
            : WindowsExecutableRoleClassifier.Classify(path);
        if (role != ExecutableRole.Unknown)
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.ExecutableRole,
                role.IsHelperLike() ? -1.0 : 0.35,
                hasUserOverride ? $"User role override: {role}" : role.ToString()));
        }

        var inGameConfigStore = _gameConfigStore.ContainsExecutable(path);
        if (inGameConfigStore)
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.WindowsGameConfigStore,
                0.55,
                "HKCU GameConfigStore exact executable path"));
        }

        if (path.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.UnrealRuntime,
                0.60,
                "Unreal Shipping executable layout"));
        }
        else
        {
            var directory = Path.GetDirectoryName(path);
            var baseName = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(directory) &&
                (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) ||
                 Directory.Exists(Path.Combine(directory, $"{baseName}_Data"))))
            {
                evidence.Add(new GameDetectionEvidence(
                    GameDetectionEvidenceKind.UnityRuntime,
                    0.55,
                    "Unity runtime layout"));
            }
        }

        AddFilenameEvidence(path, evidence);

        var hasGraphicsRuntime = false;
        var hasVisibleWindow = false;
        var isForegroundWindow = false;
        if (_inspectLiveProcess && process.ProcessId > 0)
        {
            AddProcessRelationshipEvidence(normalizedProcess, observedAtUtc, evidence);
            InspectLiveProcess(
                process.ProcessId,
                evidence,
                out hasGraphicsRuntime,
                out hasVisibleWindow,
                out isForegroundWindow);
        }

        return new WindowsProcessEvidence(
            role,
            evidence,
            inGameConfigStore,
            hasGraphicsRuntime,
            hasVisibleWindow,
            isForegroundWindow);
    }

    private void AddProcessRelationshipEvidence(
        ProcessSnapshot process,
        DateTimeOffset observedAtUtc,
        List<GameDetectionEvidence> evidence)
    {
        var parentProcessId = _parentProvider.TryGetParentProcessId(process.ProcessId);
        if (parentProcessId is null || parentProcessId <= 0 || parentProcessId == process.ProcessId)
        {
            return;
        }

        var liveParentPath = NormalizeExecutablePath(_parentProvider.TryGetExecutablePath(parentProcessId.Value));
        if (liveParentPath is not null)
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.ProcessRelationship,
                0.0,
                liveParentPath));
            return;
        }

        var recentParentPath = _relationshipHistory.TryGetExecutablePath(
            parentProcessId.Value,
            observedAtUtc,
            process.StartedAtUtc);
        if (recentParentPath is null)
        {
            return;
        }

        evidence.Add(new GameDetectionEvidence(
            GameDetectionEvidenceKind.ProcessRelationshipHistory,
            0.0,
            recentParentPath));
    }

    private static void InspectLiveProcess(
        int processId,
        List<GameDetectionEvidence> evidence,
        out bool hasGraphicsRuntime,
        out bool hasVisibleWindow,
        out bool isForegroundWindow)
    {
        hasGraphicsRuntime = false;
        hasVisibleWindow = false;
        isForegroundWindow = false;

        try
        {
            using var process = Process.GetProcessById(processId);
            var mainWindow = process.MainWindowHandle;
            hasVisibleWindow = mainWindow != IntPtr.Zero;
            if (hasVisibleWindow)
            {
                evidence.Add(new GameDetectionEvidence(
                    GameDetectionEvidenceKind.VisibleWindow,
                    0.10,
                    "Process owns a top-level window"));

                isForegroundWindow = GetForegroundWindow() == mainWindow;
                if (isForegroundWindow)
                {
                    evidence.Add(new GameDetectionEvidence(
                        GameDetectionEvidenceKind.ForegroundWindow,
                        0.10,
                        "Process owns the foreground window"));
                }
            }

            try
            {
                foreach (ProcessModule module in process.Modules)
                {
                    if (GraphicsModules.Contains(module.ModuleName))
                    {
                        hasGraphicsRuntime = true;
                        break;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
            {
            }

            if (hasGraphicsRuntime)
            {
                evidence.Add(new GameDetectionEvidence(
                    GameDetectionEvidenceKind.GraphicsRuntime,
                    0.15,
                    "Direct3D/OpenGL/Vulkan module loaded"));
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
        }
    }

    private static void AddFilenameEvidence(string executablePath, List<GameDetectionEvidence> evidence)
    {
        var directory = Path.GetDirectoryName(executablePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        var executable = NormalizeToken(Path.GetFileNameWithoutExtension(executablePath));
        var folder = NormalizeToken(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
        if (executable.Length >= 4 && folder.Length >= 4 &&
            (executable.Contains(folder, StringComparison.OrdinalIgnoreCase) ||
             folder.Contains(executable, StringComparison.OrdinalIgnoreCase)))
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.FilenameHeuristic,
                0.10,
                "Executable name resembles its install folder"));
        }
    }

    private static string? NormalizeExecutablePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath.Trim().Trim('"'));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
