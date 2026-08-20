using GameHours.Core.Updates;

namespace GameHours.Core.Abstractions;

public interface IAppUpdateService
{
    bool IsInstalled { get; }

    string? CurrentVersion { get; }

    string Channel { get; }

    Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default);

    Task DownloadAsync(
        AppUpdate update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);

    void PrepareApplyAndRestart(
        AppUpdate update,
        string[]? restartArgs = null);
}
