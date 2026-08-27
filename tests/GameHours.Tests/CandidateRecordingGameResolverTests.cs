using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;

namespace GameHours.Tests;

public sealed class CandidateRecordingGameResolverTests
{
    [Fact]
    public async Task InstalledPathCandidateIsRecordedWithoutChangingResolution()
    {
        var repository = new FakeCandidateRepository();
        var expected = new GameResolution(
            null,
            0.70,
            "installed_path_candidate",
            false,
            ExecutableRole.SecondaryGame,
            new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.InstalledGamePath, 0.90, "known install") });
        var resolver = new CandidateRecordingGameResolver(new StaticResolver(expected), repository);
        var path = Path.Combine(Path.GetTempPath(), "SomeLauncherLibrary", "KnownGame", "worker.exe");

        var actual = await resolver.ResolveAsync(new ProcessSnapshot(10, "worker", path, null));

        Assert.Equal(expected, actual);
        Assert.Single(repository.Observed);
    }

    [Fact]
    public async Task GenericGraphicsApplicationOutsideGameLocationIsNotRecorded()
    {
        var repository = new FakeCandidateRepository();
        var expected = GraphicsCandidate();
        var resolver = new CandidateRecordingGameResolver(new StaticResolver(expected), repository);
        var path = Path.Combine(Path.GetTempPath(), "Programs", "Browser", "browser.exe");

        await resolver.ResolveAsync(new ProcessSnapshot(11, "browser", path, null));

        Assert.Empty(repository.Observed);
    }

    [Fact]
    public async Task GenericGraphicsApplicationInsideGamesFolderCanBeRecorded()
    {
        var repository = new FakeCandidateRepository();
        var expected = GraphicsCandidate();
        var resolver = new CandidateRecordingGameResolver(new StaticResolver(expected), repository);
        var path = Path.Combine(Path.GetTempPath(), "Games", "UnknownTitle", "unknown.exe");

        await resolver.ResolveAsync(new ProcessSnapshot(12, "unknown", path, null));

        Assert.Single(repository.Observed);
    }

    [Fact]
    public async Task WeakFilenameEvidenceAloneIsNeverRecorded()
    {
        var repository = new FakeCandidateRepository();
        var expected = new GameResolution(
            null,
            0.10,
            "unresolved",
            false,
            ExecutableRole.Unknown,
            new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.FilenameHeuristic, 0.10, "name") });
        var resolver = new CandidateRecordingGameResolver(new StaticResolver(expected), repository);

        await resolver.ResolveAsync(new ProcessSnapshot(13, "tool", Path.Combine(Path.GetTempPath(), "Games", "tool.exe"), null));

        Assert.Empty(repository.Observed);
    }

    [Fact]
    public async Task TrackableAndHelperResolutionsAreNeverCandidates()
    {
        var repository = new FakeCandidateRepository();
        var path = Path.Combine(Path.GetTempPath(), "Games", "game.exe");
        var strong = new CandidateRecordingGameResolver(new StaticResolver(new GameResolution(null, 0.90, "strong", false, ExecutableRole.PrimaryGame, new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.WindowsGameConfigStore, 0.55, "config") })), repository);
        await strong.ResolveAsync(new ProcessSnapshot(14, "game", path, null));
        var helper = new CandidateRecordingGameResolver(new StaticResolver(new GameResolution(null, 0.10, "helper", true, ExecutableRole.Helper, new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.ExecutableRole, -1, "helper") })), repository);
        await helper.ResolveAsync(new ProcessSnapshot(15, "helper", path, null));
        Assert.Empty(repository.Observed);
    }

    private static GameResolution GraphicsCandidate() => new(
        null,
        0.65,
        "heuristic_graphics_candidate",
        false,
        ExecutableRole.Unknown,
        new[]
        {
            new GameDetectionEvidence(GameDetectionEvidenceKind.GraphicsRuntime, 0.15, "graphics"),
            new GameDetectionEvidence(GameDetectionEvidenceKind.VisibleWindow, 0.10, "window")
        });

    private sealed class StaticResolver(GameResolution resolution) : IGameResolver
    {
        public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default) => Task.FromResult(resolution);
    }

    private sealed class FakeCandidateRepository : IGameCandidateRepository
    {
        public List<GameCandidateObservation> Observed { get; } = new();
        public Task InitializeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ObserveAsync(GameCandidateObservation observation, CancellationToken cancellationToken = default) { Observed.Add(observation); return Task.CompletedTask; }
        public Task<IReadOnlyList<GameCandidate>> GetPendingAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GameCandidate>>(Array.Empty<GameCandidate>());
        public Task<int> GetPendingCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task ResolveAsync(string executablePath, ExecutableRole decisionRole, Guid? gameId = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
