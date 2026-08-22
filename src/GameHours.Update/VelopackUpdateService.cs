using GameHours.Core.Abstractions;
using GameHours.Core.Updates;
using Velopack;
using Velopack.Locators;

namespace GameHours.Update;

public sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _lastCheckedUpdate;

    public VelopackUpdateService(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Update source cannot be empty.", nameof(source));
        }

        _manager = new UpdateManager(source.Trim());
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();
    public string Channel => VelopackLocator.IsCurrentSet
        ? VelopackLocator.Current.Channel ?? "unknown"
        : "unknown";

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsInstalled)
        {
            _lastCheckedUpdate = null;
            return null;
        }

        var update = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
        _lastCheckedUpdate = update;
        if (update is null) return null;

        var target = update.TargetFullRelease;
        return new AppUpdate(
            target.Version.ToString(),
            string.IsNullOrWhiteSpace(target.NotesMarkdown) ? null : target.NotesMarkdown,
            target.Size,
            update.DeltasToTarget.Length,
            update.IsDowngrade);
    }

    public async Task DownloadAsync(
        AppUpdate update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        var checkedUpdate = RequireCheckedUpdate(update);
        await _manager.DownloadUpdatesAsync(
            checkedUpdate,
            progress is null ? null : value => progress.Report(value),
            cancellationToken);
    }

    public void PrepareApplyAndRestart(AppUpdate update, string[]? restartArgs = null)
    {
        ArgumentNullException.ThrowIfNull(update);
        RequireCheckedUpdate(update);
        var pending = _manager.UpdatePendingRestart;
        if (pending is null || !string.Equals(pending.Version.ToString(), update.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Update {update.Version} has not been downloaded and prepared yet.");
        }

        _manager.WaitExitThenApplyUpdates(
            pending,
            silent: false,
            restart: true,
            restartArgs: restartArgs);
    }

    private UpdateInfo RequireCheckedUpdate(AppUpdate update)
    {
        if (_lastCheckedUpdate is null ||
            !string.Equals(_lastCheckedUpdate.TargetFullRelease.Version.ToString(), update.Version, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("The requested update is stale. Check for updates again before downloading or applying it.");
        }

        return _lastCheckedUpdate;
    }
}
