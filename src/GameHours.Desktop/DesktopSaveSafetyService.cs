using GameHours.Core.Domain;
using GameHours.SaveSafety;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

internal enum DesktopSaveSafetyPreviewStatus
{
    Ready,
    Unsupported,
    Ambiguous,
    NoSaveData,
    Error
}

internal sealed record DesktopSaveSafetyPreview(
    DesktopSaveSafetyPreviewStatus Status,
    string Summary,
    string Detail,
    SaveDataPreview? Data = null);

internal enum DesktopSaveSafetyBackupStatus
{
    Success,
    NoChanges,
    Partial,
    Error
}

internal sealed record DesktopSaveSafetyBackup(
    DesktopSaveSafetyBackupStatus Status,
    string Summary,
    string Detail,
    SaveBackupResult? Data = null);

internal sealed class DesktopSaveSafetyService
{
    private readonly string _enginePath;
    private readonly string _manifestPath;
    private readonly string _backupRoot;
    private readonly SqliteSaveSafetyStateRepository _stateRepository;

    public DesktopSaveSafetyService(
        string? baseDirectory = null,
        string? databasePath = null,
        string? backupRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        _enginePath = Path.Combine(root, "tools", "GameHours.SaveEngine.exe");
        _manifestPath = Path.Combine(root, "tools", "ludusavi-manifest.yaml");
        var gameHoursData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours");
        _backupRoot = Path.GetFullPath(backupRoot ?? Path.Combine(gameHoursData, "save-safety"));
        _stateRepository = new SqliteSaveSafetyStateRepository(
            new GameHoursDatabase(databasePath ?? Path.Combine(gameHoursData, "gamehours.db")));
    }

    public Task<SaveSafetyState?> GetStateAsync(
        Guid gameId,
        CancellationToken cancellationToken = default) =>
        _stateRepository.GetAsync(gameId, cancellationToken);

    public async Task<DesktopSaveSafetyBackup> BackupAsync(
        Guid gameId,
        GameDiscoverySource? source,
        string? externalId,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        if (!File.Exists(_enginePath) || !File.Exists(_manifestPath))
        {
            return new(
                DesktopSaveSafetyBackupStatus.Error,
                "Save Safety no está disponible en esta instalación.",
                "Falta el motor integrado o su manifest de ubicaciones de partidas.");
        }

        if (!TryCreateEngineRequest(source, externalId, installDirectory, out var identity, out var root, out var reason))
        {
            return new(DesktopSaveSafetyBackupStatus.Error, "No se puede crear la copia.", reason);
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        SaveSafetyState? previous = null;
        try
        {
            previous = await _stateRepository.GetAsync(gameId, cancellationToken);
            Directory.CreateDirectory(_backupRoot);
            var result = await new SaveEngineClient(_enginePath, timeout: TimeSpan.FromMinutes(2))
                .CreateGameBackupAsync(
                    _manifestPath,
                    identity!,
                    [root!],
                    _backupRoot,
                    cancellationToken);

            var status = result.Partial
                ? SaveSafetyOperationStatus.Partial
                : SaveSafetyOperationStatus.Success;
            var persisted = new SaveSafetyState(
                gameId,
                attemptedAtUtc,
                status == SaveSafetyOperationStatus.Success ? attemptedAtUtc : previous?.LastSuccessAtUtc,
                status,
                LastErrorCode: null,
                result.FileCount,
                result.TotalBytes,
                result.Changed,
                DateTimeOffset.UtcNow);
            string? persistenceWarning = null;
            try
            {
                await _stateRepository.UpsertAsync(persisted, cancellationToken);
            }
            catch (Exception error) when (error is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                persistenceWarning = " La copia se completó, pero GameHours no pudo registrar su estado local.";
            }

            if (result.Partial)
            {
                var failures = result.FailedFileCount + result.FailedRegistryKeyCount;
                return new(
                    DesktopSaveSafetyBackupStatus.Partial,
                    "Copia creada con incidencias.",
                    $"{failures} elementos no se pudieron proteger completamente. {BuildBackupPayloadDetail(result)}{persistenceWarning}",
                    result);
            }

            return result.Changed
                ? new(
                    DesktopSaveSafetyBackupStatus.Success,
                    "Copia manual creada correctamente.",
                    BuildBackupPayloadDetail(result) + persistenceWarning,
                    result)
                : new(
                    DesktopSaveSafetyBackupStatus.NoChanges,
                    "No había cambios que copiar.",
                    "Los datos protegibles coinciden con la última copia disponible." + persistenceWarning,
                    result);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SaveEngineException error)
        {
            try
            {
                await _stateRepository.UpsertAsync(new SaveSafetyState(
                    gameId,
                    attemptedAtUtc,
                    previous?.LastSuccessAtUtc,
                    SaveSafetyOperationStatus.Failed,
                    error.Code,
                    LastFileCount: null,
                    LastTotalBytes: null,
                    LastChanged: null,
                    DateTimeOffset.UtcNow), CancellationToken.None);
            }
            catch (Exception persistenceError) when (persistenceError is InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                // The engine error remains authoritative; persistence must not hide it.
            }

            return new(
                DesktopSaveSafetyBackupStatus.Error,
                "No se pudo crear la copia manual.",
                $"Código: {error.Code}. Tus partidas originales no se han modificado.");
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            return new(
                DesktopSaveSafetyBackupStatus.Error,
                "No se pudo crear la copia manual.",
                "GameHours no pudo preparar el almacenamiento local de Save Safety. Tus partidas originales no se han modificado.");
        }
    }

    public async Task<DesktopSaveSafetyPreview> PreviewAsync(
        GameDiscoverySource? source,
        string? externalId,
        string? installDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_enginePath) || !File.Exists(_manifestPath))
        {
            return new(
                DesktopSaveSafetyPreviewStatus.Error,
                "Save Safety no está disponible en esta instalación.",
                "Falta el motor integrado o su manifest de ubicaciones de partidas.");
        }

        if (!TryCreateEngineRequest(source, externalId, installDirectory, out var identity, out var root, out var reason))
        {
            return new(DesktopSaveSafetyPreviewStatus.Unsupported, "Este juego aún no tiene una identidad compatible.", reason);
        }

        try
        {
            var preview = await new SaveEngineClient(_enginePath).PreviewGameSaveDataAsync(
                _manifestPath,
                identity!,
                [root!],
                cancellationToken);
            return new(
                DesktopSaveSafetyPreviewStatus.Ready,
                $"Datos de guardado detectados para {preview.GameName}",
                BuildReadyDetail(preview),
                preview);
        }
        catch (SaveEngineException error) when (error.Code == "UnsupportedGame")
        {
            return new(DesktopSaveSafetyPreviewStatus.Unsupported, "Juego no compatible por ahora.", error.Message);
        }
        catch (SaveEngineException error) when (error.Code == "AmbiguousGame")
        {
            return new(DesktopSaveSafetyPreviewStatus.Ambiguous, "La identidad del juego es ambigua.", error.Message);
        }
        catch (SaveEngineException error) when (error.Code == "NoSaveData")
        {
            return new(DesktopSaveSafetyPreviewStatus.NoSaveData, "No se han encontrado partidas guardadas.", "El juego está identificado, pero el motor no encontró datos de guardado en las ubicaciones conocidas.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (SaveEngineException error)
        {
            return new(
                DesktopSaveSafetyPreviewStatus.Error,
                "No se pudo revisar las partidas guardadas.",
                $"Código: {error.Code}. Puedes reintentar; el seguimiento de tiempo no se ve afectado.");
        }
    }

    internal static string BuildReadyDetail(SaveDataPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);

        var readableSize = FormatBytes(preview.TotalBytes);
        var failedFiles = preview.Files.Count(file => file.Failed);
        var fileLabel = preview.FileCount == 1 ? "archivo asociado" : "archivos asociados";
        var registryLabel = preview.RegistryKeyCount == 1 ? "clave de registro" : "claves de registro";
        var health = failedFiles == 0
            ? $"{preview.RegistryKeyCount} {registryLabel}"
            : $"{failedFiles} no se pudieron leer completamente";

        return $"{readableSize} en {preview.FileCount} {fileLabel} · {health}. " +
               "El total incluye todo lo que el motor protegería (por ejemplo partidas, miniaturas, configuración o copias sincronizadas) y no equivale al número de partidas.";
    }

    internal static string FormatPersistedState(SaveSafetyState? state)
    {
        if (state is null) return "Aún no hay ninguna copia manual registrada para este juego.";

        var local = state.LastAttemptAtUtc.ToLocalTime().ToString("dd/MM/yyyy HH:mm", System.Globalization.CultureInfo.InvariantCulture);
        return state.LastStatus switch
        {
            SaveSafetyOperationStatus.Success when state.LastChanged == false =>
                $"Último intento correcto: {local} · no había cambios nuevos.",
            SaveSafetyOperationStatus.Success =>
                $"Última copia correcta: {local} · {FormatPersistedPayload(state)}.",
            SaveSafetyOperationStatus.Partial =>
                $"Última copia con incidencias: {local} · {FormatPersistedPayload(state)}.",
            SaveSafetyOperationStatus.Failed =>
                $"Último intento fallido: {local} · código {state.LastErrorCode}.",
            _ => "El último estado de Save Safety no es reconocible."
        };
    }

    private static string BuildBackupPayloadDetail(SaveBackupResult result) =>
        $"{FormatBytes(result.TotalBytes)} en {result.FileCount} {(result.FileCount == 1 ? "archivo asociado" : "archivos asociados")}.";

    private static string FormatPersistedPayload(SaveSafetyState state) =>
        state.LastFileCount is { } files && state.LastTotalBytes is { } bytes
            ? $"{FormatBytes(bytes)} en {files} {(files == 1 ? "archivo asociado" : "archivos asociados")}"
            : "sin métricas de payload";

    internal static bool TryCreateEngineRequest(
        GameDiscoverySource? source,
        string? externalId,
        string? installDirectory,
        out SaveEngineGameIdentity? identity,
        out SaveEngineRoot? root,
        out string reason)
    {
        identity = null;
        root = null;

        if (source is null || string.IsNullOrWhiteSpace(externalId) || string.IsNullOrWhiteSpace(installDirectory))
        {
            reason = "GameHours no conserva una identidad de tienda verificada para esta instalación.";
            return false;
        }

        var install = new DirectoryInfo(Path.GetFullPath(installDirectory));
        switch (source)
        {
            case GameDiscoverySource.Steam:
            {
                var steamApps = install.Parent?.Parent;
                var library = steamApps?.Parent;
                if (steamApps is null || library is null ||
                    !steamApps.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
                {
                    reason = "La instalación de Steam no tiene una raíz de biblioteca reconocible.";
                    return false;
                }

                identity = new SaveEngineGameIdentity("steam", externalId.Trim());
                root = new SaveEngineRoot(library.FullName, "steam");
                reason = string.Empty;
                return true;
            }
            case GameDiscoverySource.Gog:
            {
                var library = install.Parent;
                if (library is null)
                {
                    reason = "La instalación de GOG no tiene una raíz de biblioteca reconocible.";
                    return false;
                }

                identity = new SaveEngineGameIdentity("gog", externalId.Trim());
                root = new SaveEngineRoot(library.FullName, "gog");
                reason = string.Empty;
                return true;
            }
            default:
                reason = "Save Safety solo usa IDs estables de Steam y GOG en esta primera preview; no se adivina por título.";
                return false;
        }
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }
}
