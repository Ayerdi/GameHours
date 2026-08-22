using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;

namespace GameHours.Tests;

public sealed class GameCandidateRepositoryTests
{
    [Fact]
    public async Task PendingCandidatePersistsAndDecisionPreventsReappearing()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "GameHoursTests",
            Guid.NewGuid().ToString("N"),
            "candidate.db");
        var database = new GameHoursDatabase(databasePath);
        await database.InitializeAsync();
        var repository = new SqliteGameCandidateRepository(database);
        await repository.InitializeAsync();
        var executable = Path.Combine(Path.GetTempPath(), "Games", "Unknown", "game.exe");
        var observation = new GameCandidateObservation(
            executable,
            "game",
            "Unknown Game",
            0.65,
            "heuristic_graphics_candidate",
            ExecutableRole.Unknown,
            new[]
            {
                new GameDetectionEvidence(
                    GameDetectionEvidenceKind.GraphicsRuntime,
                    0.15,
                    "Direct3D module loaded")
            },
            DateTimeOffset.UtcNow);

        await repository.ObserveAsync(observation);
        var pending = await repository.GetPendingAsync();

        Assert.Single(pending);
        Assert.Equal(Path.GetFullPath(executable), pending[0].ExecutablePath);
        Assert.Equal(1, await repository.GetPendingCountAsync());

        await repository.ResolveAsync(executable, ExecutableRole.Ignored);
        await repository.ObserveAsync(observation with { ObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(1) });

        Assert.Empty(await repository.GetPendingAsync());
        Assert.Equal(0, await repository.GetPendingCountAsync());

        try
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public async Task RepeatedPendingObservationUpdatesConfidenceAndCount()
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            "GameHoursTests",
            Guid.NewGuid().ToString("N"),
            "candidate.db");
        var database = new GameHoursDatabase(databasePath);
        await database.InitializeAsync();
        var repository = new SqliteGameCandidateRepository(database);
        await repository.InitializeAsync();
        var executable = Path.Combine(Path.GetTempPath(), "Games", "Candidate", "candidate.exe");
        var firstAt = DateTimeOffset.UtcNow.AddSeconds(-5);

        await repository.ObserveAsync(new GameCandidateObservation(
            executable,
            "candidate",
            "Candidate",
            0.20,
            "unresolved",
            ExecutableRole.Unknown,
            Array.Empty<GameDetectionEvidence>(),
            firstAt));
        await repository.ObserveAsync(new GameCandidateObservation(
            executable,
            "candidate",
            "Candidate Game",
            0.65,
            "heuristic_graphics_candidate",
            ExecutableRole.Unknown,
            Array.Empty<GameDetectionEvidence>(),
            firstAt.AddSeconds(5)));

        var candidate = Assert.Single(await repository.GetPendingAsync());
        Assert.Equal(2, candidate.ObservationCount);
        Assert.Equal(0.65, candidate.Confidence, 3);
        Assert.Equal("Candidate Game", candidate.SuggestedTitle);
        Assert.Equal("heuristic_graphics_candidate", candidate.Method);

        try
        {
            Directory.Delete(Path.GetDirectoryName(databasePath)!, recursive: true);
        }
        catch
        {
        }
    }
}
