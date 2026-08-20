using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class ManualGameRegistrationServiceTests
{
    [Fact]
    public async Task RegistersExecutableAndReusesExistingGameTitle()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var executablePath = Path.Combine(directory, "ProjectPIIT.exe");
        await File.WriteAllBytesAsync(executablePath, Array.Empty<byte>());

        try
        {
            var existing = new TrackedGame(Guid.NewGuid(), "Project P.I.I.T.");
            var games = new FakeGameRepository();
            await games.UpsertAsync(existing);
            var mappings = new FakeMappingRepository();
            var service = new ManualGameRegistrationService(games, mappings);

            var registered = await service.RegisterAsync(executablePath, "Project P.I.I.T.");
            var mapping = await mappings.FindByPathAsync(executablePath);

            Assert.Equal(existing.Id, registered.Id);
            Assert.Equal(existing.Id, mapping?.GameId);
            Assert.False(mapping?.IsHelper);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task RejectsNonExecutableFile()
    {
        var directory = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "not-a-game.txt");
        await File.WriteAllTextAsync(path, "test");

        try
        {
            var service = new ManualGameRegistrationService(
                new FakeGameRepository(),
                new FakeMappingRepository());

            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.RegisterAsync(path, "Not a game"));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private sealed class FakeMappingRepository : IExecutableMappingRepository
    {
        private readonly Dictionary<string, ExecutableMapping> _items =
            new(StringComparer.OrdinalIgnoreCase);

        public Task<ExecutableMapping?> FindByPathAsync(string executablePath, CancellationToken cancellationToken = default)
        {
            _items.TryGetValue(Path.GetFullPath(executablePath), out var mapping);
            return Task.FromResult(mapping);
        }

        public Task UpsertAsync(ExecutableMapping mapping, CancellationToken cancellationToken = default)
        {
            _items[mapping.ExecutablePath] = mapping;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeGameRepository : IGameRepository
    {
        private readonly List<TrackedGame> _games = new();

        public Task UpsertAsync(TrackedGame game, CancellationToken cancellationToken = default)
        {
            var index = _games.FindIndex(item => item.Id == game.Id);
            if (index >= 0)
            {
                _games[index] = game;
            }
            else
            {
                _games.Add(game);
            }

            return Task.CompletedTask;
        }

        public Task<TrackedGame?> GetByIdAsync(Guid gameId, CancellationToken cancellationToken = default) =>
            Task.FromResult(_games.FirstOrDefault(game => game.Id == gameId));

        public Task<TrackedGame?> GetByTitleAsync(string title, CancellationToken cancellationToken = default) =>
            Task.FromResult(_games.FirstOrDefault(game =>
                string.Equals(game.Title, title, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<TrackedGame>> GetAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TrackedGame>>(_games.ToArray());
    }
}
