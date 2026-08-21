using GameHours.Storage.Sqlite;
using GameHours.Windows.Achievements;

namespace GameHours.Desktop;

internal sealed class DesktopAchievementCoordinator
{
    private readonly LocalAchievementObservationService _observationService;

    public DesktopAchievementCoordinator(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        }

        var database = new GameHoursDatabase(databasePath);
        var repository = new SqliteAchievementRepository(database);
        _observationService = new LocalAchievementObservationService(
            new AggregatingLocalAchievementProvider(),
            repository);
    }

    public Task<LocalAchievementObservationResult?> ObserveAsync(
        Guid gameId,
        string executablePath,
        CancellationToken cancellationToken = default) =>
        _observationService.ObserveAsync(
            gameId,
            executablePath,
            DateTimeOffset.UtcNow,
            cancellationToken);
}
