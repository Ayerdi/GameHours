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
                var game = await _games.GetByIdAsync(learned.GameId, cancellationToken);
                if (game is not null)
                {
                    return new GameResolution(
                        game,
                        1.0,
                        "learned_executable_path",
                        learned.IsHelper);
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

        await _games.UpsertAsync(resolution.Game, cancellationToken);
        await _mappings.UpsertAsync(
            new ExecutableMapping(
                resolution.Game.Id,
                executablePath,
                resolution.IsHelper),
            cancellationToken);

        return resolution;
    }

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
