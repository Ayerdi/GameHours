using GameHours.Core.Abstractions;
using GameHours.Core.Domain;
using GameHours.Core.Monitoring;

namespace GameHours.Core.Discovery;

public sealed class LearningGameResolver : IGameResolver
{
    private readonly IGameResolver _inner;
    private readonly IExecutableMappingRepository _mappings;
    private readonly IGameRepository _games;
    private readonly double _minimumLearningConfidence;

    public LearningGameResolver(
        IGameResolver inner,
        IExecutableMappingRepository mappings,
        IGameRepository games,
        double minimumLearningConfidence = 0.80)
    {
        if (minimumLearningConfidence is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumLearningConfidence));
        }

        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _minimumLearningConfidence = minimumLearningConfidence;
    }

    public async Task<GameResolution> ResolveAsync(
        ProcessSnapshot process,
        CancellationToken cancellationToken = default)
    {
        var executablePath = NormalizePath(process.ExecutablePath);
        if (executablePath is not null)
        {
            var learned = await _mappings.FindByPathAsync(executablePath, cancellationToken);
            if (learned is not null)
            {
                var mappedGame = await _games.GetByIdAsync(learned.GameId, cancellationToken);
                if (mappedGame is not null)
                {
                    var canonical = await _games.GetByTitleAsync(mappedGame.Title, cancellationToken)
                        ?? mappedGame;

                    if (canonical.Id != learned.GameId)
                    {
                        await _mappings.UpsertAsync(
                            new ExecutableMapping(canonical.Id, executablePath, learned.IsHelper),
                            cancellationToken);
                    }

                    return new GameResolution(
                        canonical,
                        1.0,
                        "learned_executable_path",
                        learned.IsHelper,
                        learned.IsHelper ? ExecutableRole.Helper : ExecutableRole.PrimaryGame,
                        new[]
                        {
                            new GameDetectionEvidence(
                                GameDetectionEvidenceKind.LearnedExecutablePath,
                                1.0,
                                "Exact executable path learned locally")
                        });
                }
            }
        }

        var resolution = await _inner.ResolveAsync(process, cancellationToken);
        if (executablePath is null ||
            resolution.Game is null ||
            resolution.Confidence < _minimumLearningConfidence)
        {
            return resolution;
        }

        var game = resolution.Game;
        if (IsLooseRuntimeResolution(resolution.Method))
        {
            game = await _games.GetByTitleAsync(game.Title, cancellationToken) ?? game;
            resolution = resolution with { Game = game };
        }

        await _games.UpsertAsync(game, cancellationToken);
        await _mappings.UpsertAsync(
            new ExecutableMapping(
                game.Id,
                executablePath,
                resolution.IsHelperProcess),
            cancellationToken);

        return resolution;
    }

    private static bool IsLooseRuntimeResolution(string method) =>
        method.StartsWith("loose_", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
