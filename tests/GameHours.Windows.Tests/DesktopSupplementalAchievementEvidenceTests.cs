using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Achievements;
using GameHours.Windows.Achievements.Evidence;
using Microsoft.Data.Sqlite;

namespace GameHours.Windows.Tests;

public sealed class DesktopSupplementalAchievementEvidenceTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "gamehours-desktop-evidence",
        Guid.NewGuid().ToString("N"));
    private string _databasePath = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "gamehours.db");
        await new GameHoursDatabase(_databasePath).InitializeAsync();
    }

    public Task DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task CoordinatorWithoutSupplementalProvidersIsTrueNoOp()
    {
        var coordinator = new DesktopAchievementCoordinator(_databasePath);

        var result = await coordinator.ObserveSupplementalEvidenceAsync(
            Guid.Empty,
            string.Empty,
            string.Empty);

        Assert.Null(result);
    }

    [Fact]
    public async Task ActiveMonitorSamplesSupplementalEvidenceOnlyAtSessionBoundaries()
    {
        var executablePath = Path.Combine(_directory, "game.exe");
        await File.WriteAllTextAsync(executablePath, string.Empty);
        var provider = new CountingEvidenceProvider();
        var coordinator = new DesktopAchievementCoordinator(
            _databasePath,
            [provider],
            new SteamCompatibleAppIdResolver(persistentCachePath: null));
        await using var monitor = new ActiveAchievementMonitor(
            coordinator,
            CancellationToken.None,
            fallbackInterval: TimeSpan.FromMilliseconds(20),
            sourceDiscoveryInterval: TimeSpan.FromMilliseconds(20),
            eventSettleDelay: TimeSpan.Zero,
            finalFlushDelay: TimeSpan.Zero);
        var gameId = Guid.NewGuid();

        monitor.Start(
            gameId,
            "Example Game",
            executablePath,
            DateTimeOffset.UtcNow);

        await provider.FirstRead.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(120);
        Assert.Equal(1, provider.ReadCount);

        await monitor.StopAsync(gameId);

        Assert.Equal(2, provider.ReadCount);
    }

    private sealed class CountingEvidenceProvider : IAchievementUnlockEvidenceProvider
    {
        private int _readCount;
        private readonly TaskCompletionSource _firstRead =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Name => "test-save";
        public int ReadCount => Volatile.Read(ref _readCount);
        public Task FirstRead => _firstRead.Task;

        public Task<AchievementEvidenceReadResult> ReadAsync(
            AchievementEvidenceRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _readCount) == 1)
            {
                _firstRead.TrySetResult();
            }

            return Task.FromResult(
                AchievementEvidenceReadResult.NoEvidence(Name) with
                {
                    ActiveRuleIdentities =
                    [
                        new AchievementEvidenceRuleIdentity(
                            Name,
                            "ACH_TEST",
                            "test.rule",
                            1)
                    ]
                });
        }
    }
}
