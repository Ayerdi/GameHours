using GameHours.Storage.Sqlite;
using GameHours.Windows.Achievements;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Desktop;

internal sealed class DesktopAchievementCoordinator
{
    private readonly LocalAchievementObservationService _observationService;
    private readonly AchievementEvidenceObservationService? _evidenceObservationService;
    private readonly SteamCompatibleAppIdResolver? _appIdResolver;

    public DesktopAchievementCoordinator(
        string databasePath,
        IEnumerable<IAchievementUnlockEvidenceProvider>? supplementalEvidenceProviders = null,
        SteamCompatibleAppIdResolver? appIdResolver = null)
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

        var evidenceProviders = supplementalEvidenceProviders?.ToArray()
            ?? Array.Empty<IAchievementUnlockEvidenceProvider>();
        if (evidenceProviders.Length == 0)
        {
            return;
        }

        _evidenceObservationService = new AchievementEvidenceObservationService(
            new AchievementEvidenceProviderChain(evidenceProviders),
            new SqliteAchievementEvidenceRepository(database));
        _appIdResolver = appIdResolver ?? new SteamCompatibleAppIdResolver();
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

    /// <summary>
    /// Observes optional supplemental evidence without mixing it into the monotonic
    /// platform/emulator achievement state. When no providers are registered this is a true
    /// no-op: no AppID resolution, file scan or SQLite evidence query is performed.
    /// </summary>
    public Task<AchievementEvidenceObservation?> ObserveSupplementalEvidenceAsync(
        Guid gameId,
        string gameTitle,
        string executablePath,
        CancellationToken cancellationToken = default)
    {
        if (_evidenceObservationService is null)
        {
            return Task.FromResult<AchievementEvidenceObservation?>(null);
        }

        return ObserveSupplementalEvidenceCoreAsync(
            gameId,
            gameTitle,
            executablePath,
            cancellationToken);
    }

    private async Task<AchievementEvidenceObservation?> ObserveSupplementalEvidenceCoreAsync(
        Guid gameId,
        string gameTitle,
        string executablePath,
        CancellationToken cancellationToken)
    {
        var request = new AchievementEvidenceRequest(
            gameId,
            gameTitle,
            executablePath,
            _appIdResolver!.TryResolve(executablePath),
            DateTimeOffset.UtcNow);

        return await _evidenceObservationService!
            .ObserveAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }
}
