using GameHours.Core.Domain;
using GameHours.SaveSafety;

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

internal sealed class DesktopSaveSafetyPreviewService
{
    private readonly string _enginePath;
    private readonly string _manifestPath;

    public DesktopSaveSafetyPreviewService(string? baseDirectory = null)
    {
        var root = string.IsNullOrWhiteSpace(baseDirectory) ? AppContext.BaseDirectory : baseDirectory;
        _enginePath = Path.Combine(root, "tools", "GameHours.SaveEngine.exe");
        _manifestPath = Path.Combine(root, "tools", "ludusavi-manifest.yaml");
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
            var readableSize = FormatBytes(preview.TotalBytes);
            var failedFiles = preview.Files.Count(file => file.Failed);
            var detail = failedFiles == 0
                ? $"{preview.FileCount} archivos · {readableSize} · {preview.RegistryKeyCount} claves de registro"
                : $"{preview.FileCount} archivos · {readableSize} · {failedFiles} no se pudieron leer completamente";
            return new(
                DesktopSaveSafetyPreviewStatus.Ready,
                $"Partidas detectadas para {preview.GameName}",
                detail,
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
