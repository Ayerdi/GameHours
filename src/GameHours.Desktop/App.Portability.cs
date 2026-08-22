using System.Diagnostics;
using System.Reflection;
using System.Windows;
using GameHours.Storage.Portability;
using GameHours.Storage.Sqlite;

namespace GameHours.Desktop;

public partial class App
{
    internal async Task RestoreBackupAndRestartAsync(string backupPath)
    {
        if (_exiting)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(backupPath))
        {
            throw new ArgumentException("Backup path cannot be empty.", nameof(backupPath));
        }

        var databasePath = _host?.DatabasePath;
        if (string.IsNullOrWhiteSpace(databasePath))
        {
            throw new InvalidOperationException("GameHours database is not initialized.");
        }

        _exiting = true;
        _startupCancellation.Cancel();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
        }

        var safetyBackupPath = BuildPreRestoreSafetyPath(databasePath);
        Exception? restoreFailure = null;

        try
        {
            // Dispose the complete host first. This requests a graceful tracker stop, finalizes
            // any active measured session, stops achievement monitoring and cancels pending reads
            // before the live SQLite file can be replaced.
            if (_host is not null)
            {
                _host.StatusChanged -= UpdateTrayStatus;
                _host.AchievementUnlocked -= ShowAchievementUnlocked;
                await _host.DisposeAsync();
                _host = null;
            }

            var database = new GameHoursDatabase(databasePath);
            var restore = new GameHoursDataRestoreService(database);
            var result = await restore.RestoreBackupAsync(backupPath, safetyBackupPath);

            System.Windows.MessageBox.Show(
                _window,
                "La copia se restauró correctamente.\n\n" +
                $"Copia de seguridad previa:\n{result.SafetyBackupPath}\n\n" +
                "GameHours se reiniciará ahora.",
                "GameHours restaurado",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            restoreFailure = exception;
            var safetyMessage = File.Exists(safetyBackupPath)
                ? $"\n\nLa copia de seguridad previa se conserva en:\n{safetyBackupPath}"
                : string.Empty;
            System.Windows.MessageBox.Show(
                _window,
                "No se pudo completar la restauración. La base actual no se sustituye si la copia " +
                "seleccionada falla durante la validación; si el fallo ocurrió después del reemplazo, " +
                "GameHours intenta volver automáticamente a la copia previa." +
                safetyMessage +
                $"\n\n{exception.Message}\n\nGameHours se reiniciará.",
                "No se pudo restaurar GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        Exception? restartFailure = null;
        try
        {
            StartFreshProcess();
        }
        catch (Exception exception)
        {
            restartFailure = exception;
            System.Windows.MessageBox.Show(
                _window,
                "GameHours no pudo iniciarse automáticamente después de la restauración. " +
                "Ábrelo manualmente cuando cierres este mensaje.\n\n" + exception.Message,
                "No se pudo reiniciar GameHours",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            CloseAfterRestoreAttempt();
        }

        if (restoreFailure is not null && restartFailure is not null)
        {
            Environment.ExitCode = 1;
        }
    }

    private static string BuildPreRestoreSafetyPath(string databasePath)
    {
        var dataDirectory = Path.GetDirectoryName(databasePath)
            ?? throw new InvalidOperationException("Could not determine the GameHours data directory.");
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
        return Path.Combine(
            dataDirectory,
            "backups",
            $"pre-restore-{timestamp}-{Guid.NewGuid():N}.db");
    }

    private static void StartFreshProcess()
    {
        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            throw new InvalidOperationException("Could not determine the GameHours executable path.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = processPath,
            UseShellExecute = true,
            WorkingDirectory = AppContext.BaseDirectory
        };

        // Framework-dependent development launches may report the dotnet host as the current
        // process. Preserve that scenario without affecting normal installed/apphost builds.
        if (string.Equals(
                Path.GetFileNameWithoutExtension(processPath),
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            var entryAssembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(entryAssembly))
            {
                throw new InvalidOperationException("Could not determine the GameHours entry assembly.");
            }
            startInfo.ArgumentList.Add(entryAssembly);
        }

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("Windows did not start the replacement GameHours process.");
        }
    }

    private void CloseAfterRestoreAttempt()
    {
        if (_window is not null)
        {
            _window.AllowClose();
            _window.Close();
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        Shutdown();
    }
}
