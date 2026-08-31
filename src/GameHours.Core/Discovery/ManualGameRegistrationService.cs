using GameHours.Core.Abstractions;
using GameHours.Core.Domain;

namespace GameHours.Core.Discovery;

public sealed class ManualGameRegistrationService
{
    private readonly IGameRepository _games;
    private readonly IExecutableMappingRepository _mappings;

    public ManualGameRegistrationService(
        IGameRepository games,
        IExecutableMappingRepository mappings)
    {
        _games = games ?? throw new ArgumentNullException(nameof(games));
        _mappings = mappings ?? throw new ArgumentNullException(nameof(mappings));
    }

    public async Task<TrackedGame> RegisterAsync(
        string executablePath,
        string title,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("Executable path cannot be empty.", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("Game title cannot be empty.", nameof(title));
        }

        var normalizedPath = Path.GetFullPath(executablePath);
        if (!string.Equals(Path.GetExtension(normalizedPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Manual game mappings must point to a .exe file.", nameof(executablePath));
        }

        if (!File.Exists(normalizedPath))
        {
            throw new FileNotFoundException("Executable was not found.", normalizedPath);
        }

        var normalizedTitle = title.Trim();
        var game = await _games.GetByTitleAsync(normalizedTitle, cancellationToken)
            ?? new TrackedGame(
                DeterministicGameId.Create("manual-title", normalizedTitle.ToUpperInvariant()),
                normalizedTitle);

        await _games.UpsertAsync(game, cancellationToken);
        await _mappings.UpsertAsync(
            new ExecutableMapping(game.Id, normalizedPath, false),
            cancellationToken);

        return game;
    }
}
