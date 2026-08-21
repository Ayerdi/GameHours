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

public sealed record WindowsProcessEvidence(
    ExecutableRole Role,
    IReadOnlyList<GameDetectionEvidence> Evidence,
    bool IsInGameConfigStore,
    bool HasGraphicsRuntime,
    bool HasVisibleWindow,
    bool IsForegroundWindow);

public sealed class WindowsProcessEvidenceCollector
{
    private const uint Th32csSnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandleValue = new(-1);

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
    private readonly bool _inspectLiveProcess;

    public WindowsProcessEvidenceCollector(
        IWindowsGameConfigStore? gameConfigStore = null,
        bool inspectLiveProcess = true)
    {
        _gameConfigStore = gameConfigStore ?? new WindowsGameConfigStore();
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
        var evidence = new List<GameDetectionEvidence>();
        var role = WindowsExecutableRoleClassifier.Classify(path);
        if (role != ExecutableRole.Unknown)
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.ExecutableRole,
                role.IsHelperLike() ? -1.0 : 0.35,
                role.ToString()));
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

        var parentPath = TryGetParentExecutablePath(processId);
        if (!string.IsNullOrWhiteSpace(parentPath))
        {
            evidence.Add(new GameDetectionEvidence(
                GameDetectionEvidenceKind.ProcessRelationship,
                0.0,
                parentPath));
        }

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

    private static string? TryGetParentExecutablePath(int processId)
    {
        var snapshot = CreateToolhelp32Snapshot(Th32csSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == InvalidHandleValue)
        {
            return null;
        }

        try
        {
            var entry = new ProcessEntry32
            {
                Size = (uint)Marshal.SizeOf<ProcessEntry32>()
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
                    using var parent = Process.GetProcessById(checked((int)entry.ParentProcessId));
                    return parent.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or OverflowException)
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

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

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

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
