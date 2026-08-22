using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using GameHours.Windows.Processes;

namespace GameHours.Desktop;

public sealed class DesktopGameCandidateScanner : IAsyncDisposable
{
    private readonly SqliteGameCandidateRepository _candidates;
    private readonly SqliteExecutableMappingRepository _mappings;
    private readonly WindowsProcessSnapshotProvider _snapshots = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<int, DateTimeOffset?> _seenProcesses = new();
    private readonly TimeSpan _scanInterval;
    private IGameResolver? _resolver;
    private Task? _loopTask;

    public event Action? CandidatesChanged;

    public DesktopGameCandidateScanner(
        GameHoursDatabase database,
        TimeSpan? scanInterval = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _candidates = new SqliteGameCandidateRepository(database);
        _mappings = new SqliteExecutableMappingRepository(database);
        _scanInterval = scanInterval ?? TimeSpan.FromSeconds(2);
        if (_scanInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(scanInterval));
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_loopTask is not null)
        {
            return;
        }

        await _candidates.InitializeAsync(cancellationToken);
        var discovery = new InstalledGameDiscoveryService(
            new IInstalledGameSource[]
            {
                new SteamInstalledGameSource(),
                new EpicInstalledGameSource(),
                new GogInstalledGameSource()
            });
        var installedGames = await discovery.DiscoverAsync(cancellationToken);
        _resolver = new WindowsGameResolver(installedGames);
        _loopTask = RunAsync(_lifetime.Token);
    }

    public Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default) =>
        _candidates.GetPendingCountAsync(cancellationToken);

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            await ScanOnceAsync(cancellationToken);
            using var timer = new PeriodicTimer(_scanInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await ScanOnceAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            // Candidate discovery is supplementary. The tracker must remain independent.
        }
    }

    private async Task ScanOnceAsync(CancellationToken cancellationToken)
    {
        if (_resolver is null)
        {
            return;
        }

        var snapshots = await _snapshots.GetSnapshotAsync(cancellationToken);
        var currentIds = snapshots.Select(item => item.ProcessId).ToHashSet();
        foreach (var stale in _seenProcesses.Keys.Where(id => !currentIds.Contains(id)).ToArray())
        {
            _seenProcesses.Remove(stale);
        }

        var changed = false;
        foreach (var process in snapshots)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(process.ExecutablePath))
            {
                continue;
            }

            if (_seenProcesses.TryGetValue(process.ProcessId, out var startedAt) &&
                startedAt == process.StartedAtUtc)
            {
                continue;
            }

            _seenProcesses[process.ProcessId] = process.StartedAtUtc;

            string executablePath;
            try
            {
                executablePath = Path.GetFullPath(process.ExecutablePath);
            }
            catch (Exception exception) when (
                exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            if (await _mappings.FindByPathAsync(executablePath, cancellationToken) is not null)
            {
                continue;
            }

            GameResolution resolution;
            try
            {
                resolution = await _resolver.ResolveAsync(
                    process with { ExecutablePath = executablePath },
                    cancellationToken);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            if (!ShouldRecord(resolution))
            {
                continue;
            }

            var suggestedTitle = resolution.Game?.Title;
            if (string.IsNullOrWhiteSpace(suggestedTitle))
            {
                suggestedTitle = Path.GetFileNameWithoutExtension(executablePath);
            }

            await _candidates.ObserveAsync(
                new GameCandidateObservation(
                    executablePath,
                    process.ProcessName,
                    suggestedTitle,
                    resolution.Confidence,
                    resolution.Method,
                    resolution.Role,
                    resolution.DetectionEvidence,
                    DateTimeOffset.UtcNow),
                cancellationToken);
            changed = true;
        }

        if (changed)
        {
            CandidatesChanged?.Invoke();
        }
    }

    private static bool ShouldRecord(GameResolution resolution)
    {
        if (resolution.IsHelperProcess || resolution.Confidence >= 0.80)
        {
            return false;
        }

        if (string.Equals(
                resolution.Method,
                "heuristic_graphics_candidate",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(resolution.Method, "unresolved", StringComparison.OrdinalIgnoreCase)
            && resolution.DetectionEvidence.Any(item => item.Weight > 0);
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _lifetime.Dispose();
    }
}
