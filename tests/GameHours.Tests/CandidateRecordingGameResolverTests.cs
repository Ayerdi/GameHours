using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;

namespace GameHours.Tests;

public sealed class CandidateRecordingGameResolverTests
{
    [Fact]
    public async Task LowConfidenceEvidenceIsRecordedWithoutChangingResolution()
    {
        var repository = new FakeCandidateRepository();
        var expected = new GameResolution(null, 0.65, "heuristic", false, ExecutableRole.Unknown, new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.GraphicsRuntime, 0.15, "graphics") });
        var resolver = new CandidateRecordingGameResolver(new StaticResolver(expected), repository);
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "candidate.exe");
        var actual = await resolver.ResolveAsync(new ProcessSnapshot(10, "candidate", path, null));
        Assert.Equal(expected, actual);
        Assert.Single(repository.Observed);
    }

    [Fact]
    public async Task TrackableAndHelperResolutionsAreNeverCandidates()
    {
        var repository = new FakeCandidateRepository();
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "game.exe");
        var strong = new CandidateRecordingGameResolver(new StaticResolver(new GameResolution(null, 0.90, "strong", false, ExecutableRole.PrimaryGame, new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.WindowsGameConfigStore, 0.55, "config") })), repository);
        await strong.ResolveAsync(new ProcessSnapshot(11, "game", path, null));
        var helper = new CandidateRecordingGameResolver(new StaticResolver(new GameResolution(null, 0.10, "helper", true, ExecutableRole.Helper, new[] { new GameDetectionEvidence(GameDetectionEvidenceKind.ExecutableRole, -1, "helper") })), repository);
        await helper.ResolveAsync(new ProcessSnapshot(12, "helper", path, null));
        Assert.Empty(repository.Observed);
    }

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
