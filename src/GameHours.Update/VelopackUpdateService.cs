using GameHours.Core.Abstractions;
using GameHours.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace GameHours.Update;

public sealed class VelopackUpdateService : IAppUpdateService
{
    private readonly UpdateManager _manager;
    private UpdateInfo? _lastCheckedUpdate;

    public VelopackUpdateService(string source)
        : this(CreateSimpleManager(source))
    {
    }

    private VelopackUpdateService(UpdateManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public static VelopackUpdateService ForGitHubRepository(string repositoryUrl)
    {
        if (string.IsNullOrWhiteSpace(repositoryUrl))
        {
            throw new ArgumentException("GitHub update repository cannot be empty.", nameof(repositoryUrl));
        }

        var prerelease = string.Equals(ReadInstalledChannel(), "beta", StringComparison.OrdinalIgnoreCase);
        var source = new GithubSource(repositoryUrl.Trim(), accessToken: null, prerelease: prerelease);
        return new VelopackUpdateService(new UpdateManager(source));
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string? CurrentVersion => _manager.CurrentVersion?.ToString();
    public string Channel => ReadInstalledChannel();

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

    private static UpdateManager CreateSimpleManager(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            throw new ArgumentException("Update source cannot be empty.", nameof(source));
        }

        return new UpdateManager(source.Trim());
    }

    private static string ReadInstalledChannel() => VelopackLocator.IsCurrentSet
        ? VelopackLocator.Current.Channel ?? "unknown"
        : "unknown";

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
