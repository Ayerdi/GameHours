using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal sealed class DesktopDataPortabilityCoordinator
{
    private readonly GameHoursDataPortabilityService _portability;
    private readonly GameHoursPortableImportService _import;

    public DesktopDataPortabilityCoordinator(string databasePath)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new ArgumentException("Database path cannot be empty.", nameof(databasePath));
        }

        DatabasePath = Path.GetFullPath(databasePath);
        DataDirectory = Path.GetDirectoryName(DatabasePath)
            ?? throw new InvalidOperationException("Could not determine the GameHours data directory.");
        BackupsDirectory = Path.Combine(DataDirectory, "backups");
        ExportsDirectory = Path.Combine(DataDirectory, "exports");

        var database = new GameHoursDatabase(DatabasePath);
        _portability = new GameHoursDataPortabilityService(database);
        _import = new GameHoursPortableImportService(database);
    }

    public string DatabasePath { get; }
    public string DataDirectory { get; }
    public string BackupsDirectory { get; }
    public string ExportsDirectory { get; }

    public Task<GameHoursBackupResult> CreateBackupAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        _portability.CreateBackupAsync(destinationPath, cancellationToken);

    public Task<GameHoursExportResult> ExportPortableJsonAsync(
        string destinationPath,
        CancellationToken cancellationToken = default) =>
        _portability.ExportPortableJsonAsync(destinationPath, cancellationToken);

    public Task<GameHoursPortableImportPreview> AnalyzePortableImportAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        _import.AnalyzeAsync(sourcePath, cancellationToken);

    public Task<GameHoursPortableImportResult> ImportPortableJsonAsync(
        string sourcePath,
        CancellationToken cancellationToken = default) =>
        _import.ImportAsync(sourcePath, cancellationToken);

    public string BuildDefaultBackupPath(DateTimeOffset now) =>
        Path.Combine(BackupsDirectory, $"gamehours-{now:yyyyMMdd-HHmmss}.db");

    public string BuildDefaultExportPath(DateTimeOffset now) =>
        Path.Combine(ExportsDirectory, $"gamehours-{now:yyyyMMdd-HHmmss}.json");
}
