using GameHours.Core.Discovery;
using GameHours.Core.Domain;
using GameHours.Desktop;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Tests;

public sealed class CandidateDecisionPersistenceTests
{
    [Fact]
    public async Task Ignore_RemovesStalePrimaryMappingAndKeepsCandidateClosed()
    {
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            await fixture.Mappings.UpsertAsync(new ExecutableMapping(fixture.Game.Id, fixture.ExecutablePath, false));

            await fixture.Decisions.IgnoreAsync(fixture.ExecutablePath);

            Assert.Null(await fixture.Mappings.FindByPathAsync(fixture.ExecutablePath));
            var decided = Assert.IsType<GameCandidate>(await fixture.Candidates.GetByPathAsync(fixture.ExecutablePath));
            Assert.Equal(GameCandidateStatus.Ignored, decided.Status);
            Assert.Equal(ExecutableRole.Ignored, decided.DecisionRole);
            Assert.True(fixture.RoleOverrides.TryGetRole(fixture.ExecutablePath, out var role));
            Assert.Equal(ExecutableRole.Ignored, role);

            await fixture.ObserveAgainAsync();
            Assert.Empty(await fixture.Candidates.GetPendingAsync());
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task HelperWithoutGame_RemovesContradictoryPrimaryMapping()
    {
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            await fixture.Mappings.UpsertAsync(new ExecutableMapping(fixture.Game.Id, fixture.ExecutablePath, false));

            await fixture.Decisions.ClassifyHelperAsync(fixture.ExecutablePath, ExecutableRole.Launcher);

            Assert.Null(await fixture.Mappings.FindByPathAsync(fixture.ExecutablePath));
            var decided = Assert.IsType<GameCandidate>(await fixture.Candidates.GetByPathAsync(fixture.ExecutablePath));
            Assert.Equal(GameCandidateStatus.Resolved, decided.Status);
            Assert.Equal(ExecutableRole.Launcher, decided.DecisionRole);
            Assert.Null(decided.DecisionGameId);
            Assert.True(fixture.RoleOverrides.TryGetRole(fixture.ExecutablePath, out var role));
            Assert.Equal(ExecutableRole.Launcher, role);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task HelperWithGame_PersistsOnlyHelperAssociation()
    {
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            await fixture.Decisions.ClassifyHelperAsync(
                fixture.ExecutablePath,
                ExecutableRole.Helper,
                fixture.Game.Id);

            var mapping = Assert.IsType<ExecutableMapping>(await fixture.Mappings.FindByPathAsync(fixture.ExecutablePath));
            Assert.Equal(fixture.Game.Id, mapping.GameId);
            Assert.True(mapping.IsHelper);
            var decided = Assert.IsType<GameCandidate>(await fixture.Candidates.GetByPathAsync(fixture.ExecutablePath));
            Assert.Equal(ExecutableRole.Helper, decided.DecisionRole);
            Assert.Equal(fixture.Game.Id, decided.DecisionGameId);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    [Fact]
    public async Task ConfirmGame_ReplacesHelperStateWithOneTrackableMapping()
    {
        var fixture = await CandidateFixture.CreateAsync();
        try
        {
            fixture.RoleOverrides.SetRole(fixture.ExecutablePath, ExecutableRole.Launcher);
            await fixture.Mappings.UpsertAsync(new ExecutableMapping(fixture.Game.Id, fixture.ExecutablePath, true));

            await fixture.Decisions.ConfirmGameAsync(
                fixture.ExecutablePath,
                fixture.Game.Id,
                ExecutableRole.SecondaryGame);

            var mapping = Assert.IsType<ExecutableMapping>(await fixture.Mappings.FindByPathAsync(fixture.ExecutablePath));
            Assert.Equal(fixture.Game.Id, mapping.GameId);
            Assert.False(mapping.IsHelper);
            Assert.False(fixture.RoleOverrides.TryGetRole(fixture.ExecutablePath, out _));
            var decided = Assert.IsType<GameCandidate>(await fixture.Candidates.GetByPathAsync(fixture.ExecutablePath));
            Assert.Equal(GameCandidateStatus.Resolved, decided.Status);
            Assert.Equal(ExecutableRole.SecondaryGame, decided.DecisionRole);
            Assert.Equal(fixture.Game.Id, decided.DecisionGameId);
        }
        finally
        {
            fixture.Dispose();
        }
    }

    private sealed class CandidateFixture : IDisposable
    {
        private readonly string _root;
        private readonly DateTimeOffset _firstObservedAt;

        public TrackedGame Game { get; }
        public string ExecutablePath { get; }
        public SqliteGameCandidateRepository Candidates { get; }
        public SqliteExecutableMappingRepository Mappings { get; }
        public LocalExecutableRoleOverrideStore RoleOverrides { get; }
        public CandidateDecisionService Decisions { get; }

        private CandidateFixture(
            string root,
            TrackedGame game,
            string executablePath,
            SqliteGameCandidateRepository candidates,
            SqliteExecutableMappingRepository mappings,
            LocalExecutableRoleOverrideStore roleOverrides,
            CandidateDecisionService decisions,
            DateTimeOffset firstObservedAt)
        {
            _root = root;
            Game = game;
            ExecutablePath = executablePath;
            Candidates = candidates;
            Mappings = mappings;
            RoleOverrides = roleOverrides;
            Decisions = decisions;
            _firstObservedAt = firstObservedAt;
        }

        public static async Task<CandidateFixture> CreateAsync()
        {
            var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
            var database = new GameHoursDatabase(Path.Combine(root, "gamehours.db"));
            await database.InitializeAsync();
            var games = new SqliteGameRepository(database);
            var mappings = new SqliteExecutableMappingRepository(database);
            var candidates = new SqliteGameCandidateRepository(database);
            var roleOverrides = new LocalExecutableRoleOverrideStore(Path.Combine(root, "roles.json"));
            var decisions = new CandidateDecisionService(candidates, mappings, roleOverrides);
            var game = new TrackedGame(Guid.NewGuid(), "Decision Test Game");
            await games.UpsertAsync(game);
            var executablePath = Path.Combine(root, "Games", "Decision Test", "game.exe");
            var observedAt = DateTimeOffset.UtcNow;
            await candidates.ObserveAsync(Observation(executablePath, observedAt));

            return new CandidateFixture(
                root,
                game,
                executablePath,
                candidates,
                mappings,
                roleOverrides,
                decisions,
                observedAt);
        }

        public Task ObserveAgainAsync() =>
            Candidates.ObserveAsync(Observation(ExecutablePath, _firstObservedAt.AddMinutes(1)));

        private static GameCandidateObservation Observation(string path, DateTimeOffset observedAt) =>
            new(
                path,
                "game",
                "Decision Test Game",
                0.65,
                "heuristic_graphics_candidate",
                ExecutableRole.Unknown,
                new[]
                {
                    new GameDetectionEvidence(GameDetectionEvidenceKind.GraphicsRuntime, 0.15, "graphics"),
                    new GameDetectionEvidence(GameDetectionEvidenceKind.VisibleWindow, 0.10, "window")
                },
                observedAt);

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
