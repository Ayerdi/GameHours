using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Windows.Processes;
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
        if (_cacheDuration < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cacheDuration));
    }

    public bool ContainsExecutable(string executablePath)
    {
        var path = PathTools.Normalize(executablePath);
        if (path is null) return false;
        lock (_gate)
        {
            if (DateTimeOffset.UtcNow - _loadedAtUtc >= _cacheDuration)
            {
                _paths = ReadPaths();
                _loadedAtUtc = DateTimeOffset.UtcNow;
            }
            return _paths.Contains(path);
        }
    }

    private static HashSet<string> ReadPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var children = Registry.CurrentUser.OpenSubKey(ChildrenKey, writable: false);
            if (children is null) return result;
            foreach (var name in children.GetSubKeyNames())
            {
                using var child = children.OpenSubKey(name, writable: false);
                if (child?.GetValue("MatchedExeFullPath") is string raw && PathTools.Normalize(raw) is { } path)
                    result.Add(path);
            }
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException) { }
        return result;
    }
}

public static class WindowsExecutableRoleClassifier
{
    private static readonly string[] CrashHandlers = ["CrashReportClient.exe", "UnityCrashHandler64.exe", "UnityCrashHandler32.exe", "crashpad_handler.exe"];
    private static readonly string[] Helpers = ["steamwebhelper.exe", "EpicWebHelper.exe", "CefSharp.BrowserSubprocess.exe", "QtWebEngineProcess.exe"];
    private static readonly string[] Launchers = ["steam.exe", "EpicGamesLauncher.exe", "GalaxyClient.exe", "GalaxyClientService.exe", "EADesktop.exe", "upc.exe", "UbisoftConnect.exe", "Battle.net.exe", "start_protected_game.exe"];
    private static readonly HashSet<string> UtilityNames = new(StringComparer.OrdinalIgnoreCase) { "config", "configuration", "settings", "benchmark", "diagnostic", "diagnostics", "configtool" };
    private static readonly HashSet<string> InstallerNames = new(StringComparer.OrdinalIgnoreCase) { "setup", "install", "installer", "repair", "redist", "prereq", "prerequisite", "uninstall" };

    public static ExecutableRole Classify(string executablePath)
    {
        var file = Path.GetFileName(executablePath);
        var name = Path.GetFileNameWithoutExtension(file);
        if (Matches(file, CrashHandlers)) return ExecutableRole.CrashHandler;
        if (file.StartsWith("EasyAntiCheat", StringComparison.OrdinalIgnoreCase) || Matches(file, "BEService.exe", "BEService_x64.exe", "vgtray.exe")) return ExecutableRole.AntiCheat;
        if (Matches(file, Helpers) || UtilityNames.Contains(name) || UtilityNames.Any(item => name.EndsWith(item, StringComparison.OrdinalIgnoreCase))) return ExecutableRole.Helper;
        if (Matches(file, Launchers) || name.EndsWith("Launcher", StringComparison.OrdinalIgnoreCase)) return ExecutableRole.Launcher;
        if (InstallerNames.Contains(name) || InstallerNames.Any(item => name.StartsWith(item, StringComparison.OrdinalIgnoreCase)) ||
            name.EndsWith("Updater", StringComparison.OrdinalIgnoreCase) || name.EndsWith("Update", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Patcher", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Unins", StringComparison.OrdinalIgnoreCase)) return ExecutableRole.Updater;
        if (file.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) || file.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase)) return ExecutableRole.PrimaryGame;

        var directory = Path.GetDirectoryName(executablePath);
        var baseName = Path.GetFileNameWithoutExtension(executablePath);
        return !string.IsNullOrWhiteSpace(directory) &&
               (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(directory, $"{baseName}_Data")))
            ? ExecutableRole.PrimaryGame
            : ExecutableRole.Unknown;
    }

    private static bool Matches(string value, params string[] candidates) =>
        candidates.Any(item => value.Equals(item, StringComparison.OrdinalIgnoreCase));
}

public interface IWindowsProcessParentProvider
{
    int? TryGetParentProcessId(int processId);
    string? TryGetExecutablePath(int processId);
}

public sealed class WindowsProcessParentProvider : IWindowsProcessParentProvider
{
    public int? TryGetParentProcessId(int processId) =>
        processId > 0 && WindowsParentProcessSnapshot.Capture().TryGetValue(processId, out var parent) ? parent : null;

    public string? TryGetExecutablePath(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainModule?.FileName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
}

public interface IRecentProcessIdentityHistory
{
    void Observe(ProcessSnapshot process, DateTimeOffset observedAtUtc);
    string? TryGetExecutablePath(int processId, DateTimeOffset observedAtUtc, DateTimeOffset? childStartedAtUtc = null);
    int? TryGetParentProcessId(int processId, DateTimeOffset observedAtUtc);
    DateTimeOffset? TryGetStartedAtUtc(int processId, DateTimeOffset observedAtUtc);
}

public static class WindowsProcessRelationshipHistory
{
    public static IRecentProcessIdentityHistory Shared { get; } = new RecentProcessIdentityHistory();
}

public sealed class RecentProcessIdentityHistory : IRecentProcessIdentityHistory
{
    private readonly object _gate = new();
    private readonly TimeSpan _retention;
    private readonly Dictionary<int, Entry> _entries = new();

    public RecentProcessIdentityHistory(TimeSpan? retention = null)
    {
        _retention = retention ?? TimeSpan.FromSeconds(30);
        if (_retention <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(retention));
    }

    public void Observe(ProcessSnapshot process, DateTimeOffset observedAtUtc)
    {
        var path = PathTools.Normalize(process.ExecutablePath);
        if (process.ProcessId <= 0 || path is null) return;
        lock (_gate)
        {
            Prune(observedAtUtc);
            if (_entries.TryGetValue(process.ProcessId, out var existing) && IsSameProcess(existing, path, process.StartedAtUtc))
            {
                _entries[process.ProcessId] = existing with
                {
                    StartedAtUtc = process.StartedAtUtc ?? existing.StartedAtUtc,
                    ParentProcessId = process.ParentProcessId ?? existing.ParentProcessId,
                    LastSeenAtUtc = observedAtUtc.ToUniversalTime()
                };
                return;
            }

            _entries[process.ProcessId] = new(path, process.StartedAtUtc, process.ParentProcessId, observedAtUtc.ToUniversalTime());
        }
    }

    public string? TryGetExecutablePath(int processId, DateTimeOffset observedAtUtc, DateTimeOffset? childStartedAtUtc = null)
    {
        lock (_gate)
        {
            if (!TryGet(processId, observedAtUtc, out var entry)) return null;
            return childStartedAtUtc is { } childStarted && entry.StartedAtUtc is { } parentStarted && parentStarted > childStarted.AddSeconds(1)
                ? null
                : entry.ExecutablePath;
        }
    }

    public int? TryGetParentProcessId(int processId, DateTimeOffset observedAtUtc)
    {
        lock (_gate) return TryGet(processId, observedAtUtc, out var entry) ? entry.ParentProcessId : null;
    }

    public DateTimeOffset? TryGetStartedAtUtc(int processId, DateTimeOffset observedAtUtc)
    {
        lock (_gate) return TryGet(processId, observedAtUtc, out var entry) ? entry.StartedAtUtc : null;
    }

    private bool TryGet(int processId, DateTimeOffset observedAtUtc, out Entry entry)
    {
        Prune(observedAtUtc);
        if (processId <= 0)
        {
            entry = null!;
            return false;
        }
        return _entries.TryGetValue(processId, out entry!);
    }

    private static bool IsSameProcess(Entry existing, string path, DateTimeOffset? startedAtUtc)
    {
        if (!string.Equals(existing.ExecutablePath, path, StringComparison.OrdinalIgnoreCase)) return false;
        return existing.StartedAtUtc is null || startedAtUtc is null || existing.StartedAtUtc == startedAtUtc;
    }

    private void Prune(DateTimeOffset observedAtUtc)
    {
        var now = observedAtUtc.ToUniversalTime();
        foreach (var pair in _entries.Where(pair => now - pair.Value.LastSeenAtUtc > _retention).ToArray()) _entries.Remove(pair.Key);
    }

    private sealed record Entry(string ExecutablePath, DateTimeOffset? StartedAtUtc, int? ParentProcessId, DateTimeOffset LastSeenAtUtc);
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
        "d3d9.dll", "d3d10.dll", "d3d11.dll", "d3d12.dll", "vulkan-1.dll", "opengl32.dll"
    };

    private readonly IWindowsGameConfigStore _gameConfigStore;
    private readonly IExecutableRoleOverrideStore _roleOverrides;
    private readonly IWindowsProcessParentProvider _parentProvider;
    private readonly IRecentProcessIdentityHistory _history;
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
        _history = relationshipHistory ?? WindowsProcessRelationshipHistory.Shared;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _inspectLiveProcess = inspectLiveProcess;
    }

    public WindowsProcessEvidence Collect(ProcessSnapshot process)
    {
        var path = PathTools.Normalize(process.ExecutablePath);
        if (path is null) return new(ExecutableRole.Unknown, Array.Empty<GameDetectionEvidence>(), false, false, false, false);

        var observedAt = _utcNow().ToUniversalTime();
        var normalized = process with { ExecutablePath = path };
        _history.Observe(normalized, observedAt);

        var evidence = new List<GameDetectionEvidence>();
        var hasOverride = _roleOverrides.TryGetRole(path, out var overriddenRole);
        var role = hasOverride ? overriddenRole : WindowsExecutableRoleClassifier.Classify(path);
        if (role != ExecutableRole.Unknown)
            evidence.Add(new(GameDetectionEvidenceKind.ExecutableRole, role.IsHelperLike() ? -1 : 0.35, hasOverride ? $"User role override: {role}" : role.ToString()));

        var inGameConfigStore = _gameConfigStore.ContainsExecutable(path);
        if (inGameConfigStore) evidence.Add(new(GameDetectionEvidenceKind.WindowsGameConfigStore, 0.55, "HKCU GameConfigStore exact executable path"));
        AddEngineEvidence(path, evidence);
        AddFilenameEvidence(path, evidence);

        var graphics = false;
        var visible = false;
        var foreground = false;
        if (_inspectLiveProcess && process.ProcessId > 0)
        {
            AddRelationshipEvidence(normalized, observedAt, evidence);
            InspectLiveProcess(process.ProcessId, evidence, out graphics, out visible, out foreground);
        }
        return new(role, evidence, inGameConfigStore, graphics, visible, foreground);
    }

    private void AddRelationshipEvidence(ProcessSnapshot process, DateTimeOffset observedAt, List<GameDetectionEvidence> evidence)
    {
        var parentId = process.ParentProcessId
            ?? _history.TryGetParentProcessId(process.ProcessId, observedAt)
            ?? _parentProvider.TryGetParentProcessId(process.ProcessId);
        if (parentId is null or <= 0 || parentId == process.ProcessId) return;

        var livePath = PathTools.Normalize(_parentProvider.TryGetExecutablePath(parentId.Value));
        if (livePath is not null)
        {
            evidence.Add(new(GameDetectionEvidenceKind.ProcessRelationship, 0, livePath));
            return;
        }

        var childStarted = process.StartedAtUtc ?? _history.TryGetStartedAtUtc(process.ProcessId, observedAt);
        if (_history.TryGetExecutablePath(parentId.Value, observedAt, childStarted) is { } recentPath)
            evidence.Add(new(GameDetectionEvidenceKind.ProcessRelationshipHistory, 0, recentPath));
    }

    private static void AddEngineEvidence(string path, List<GameDetectionEvidence> evidence)
    {
        if (path.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase) || path.EndsWith("-Win32-Shipping.exe", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new(GameDetectionEvidenceKind.UnrealRuntime, 0.60, "Unreal Shipping executable layout"));
            return;
        }

        var directory = Path.GetDirectoryName(path);
        var baseName = Path.GetFileNameWithoutExtension(path);
        if (!string.IsNullOrWhiteSpace(directory) &&
            (File.Exists(Path.Combine(directory, "UnityPlayer.dll")) || Directory.Exists(Path.Combine(directory, $"{baseName}_Data"))))
            evidence.Add(new(GameDetectionEvidenceKind.UnityRuntime, 0.55, "Unity runtime layout"));
    }

    private static void AddFilenameEvidence(string path, List<GameDetectionEvidence> evidence)
    {
        var directory = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(directory)) return;
        var executable = NormalizeToken(Path.GetFileNameWithoutExtension(path));
        var folder = NormalizeToken(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)));
        if (executable.Length >= 4 && folder.Length >= 4 &&
            (executable.Contains(folder, StringComparison.OrdinalIgnoreCase) || folder.Contains(executable, StringComparison.OrdinalIgnoreCase)))
            evidence.Add(new(GameDetectionEvidenceKind.FilenameHeuristic, 0.10, "Executable name resembles its install folder"));
    }

    private static void InspectLiveProcess(int processId, List<GameDetectionEvidence> evidence, out bool graphics, out bool visible, out bool foreground)
    {
        graphics = visible = foreground = false;
        try
        {
            using var process = Process.GetProcessById(processId);
            var window = process.MainWindowHandle;
            visible = window != IntPtr.Zero;
            if (visible)
            {
                evidence.Add(new(GameDetectionEvidenceKind.VisibleWindow, 0.10, "Process owns a top-level window"));
                foreground = GetForegroundWindow() == window;
                if (foreground) evidence.Add(new(GameDetectionEvidenceKind.ForegroundWindow, 0.10, "Process owns the foreground window"));
            }

            try { graphics = process.Modules.Cast<ProcessModule>().Any(module => GraphicsModules.Contains(module.ModuleName)); }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
            if (graphics) evidence.Add(new(GameDetectionEvidenceKind.GraphicsRuntime, 0.15, "Direct3D/OpenGL/Vulkan module loaded"));
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
    }

    private static string NormalizeToken(string value) =>
        new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}

internal static class PathTools
{
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return Path.GetFullPath(value.Trim().Trim('"')); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
