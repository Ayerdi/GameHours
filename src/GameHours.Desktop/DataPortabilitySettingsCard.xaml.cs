using System.Text;
using System.Windows;
using GameHours.Storage.Portability;

namespace GameHours.Desktop;

public partial class DataPortabilitySettingsCard : System.Windows.Controls.UserControl
{
    private readonly DesktopHost _host;
    private readonly DesktopDataPortabilityCoordinator _coordinator;
    private bool _busy;

    public DataPortabilitySettingsCard(DesktopHost host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _coordinator = new DesktopDataPortabilityCoordinator(_host.DatabasePath);
        InitializeComponent();
    }

    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Directory.CreateDirectory(_coordinator.BackupsDirectory);
        var defaultPath = _coordinator.BuildDefaultBackupPath(DateTimeOffset.Now);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Crear copia de seguridad de GameHours",
            Filter = "Base de datos SQLite (*.db)|*.db|Todos los archivos (*.*)|*.*",
            InitialDirectory = _coordinator.BackupsDirectory,
            FileName = Path.GetFileName(defaultPath),
            AddExtension = true,
            DefaultExt = ".db",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SetBusy(true, "Creando una copia consistente de la base de datos…");
        try
        {
            var result = await _coordinator.CreateBackupAsync(dialog.FileName);
            StatusTextBlock.Text =
                $"Copia creada · {FormatBytes(result.SizeBytes)} · {result.Path}";
        }
        catch (Exception exception)
        {
            ShowError("No se pudo crear la copia de seguridad", exception);
            StatusTextBlock.Text = "No se pudo crear la copia de seguridad.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Directory.CreateDirectory(_coordinator.ExportsDirectory);
        var defaultPath = _coordinator.BuildDefaultExportPath(DateTimeOffset.Now);
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar datos portables de GameHours",
            Filter = "GameHours JSON (*.json)|*.json|Todos los archivos (*.*)|*.*",
            InitialDirectory = _coordinator.ExportsDirectory,
            FileName = Path.GetFileName(defaultPath),
            AddExtension = true,
            DefaultExt = ".json",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SetBusy(true, "Exportando datos portables…");
        try
        {
            var result = await _coordinator.ExportPortableJsonAsync(dialog.FileName);
            StatusTextBlock.Text =
                $"Export v{result.FormatVersion} creado · {result.GameCount} juegos · " +
                $"{result.SessionCount} sesiones · {result.Path}";
        }
        catch (Exception exception)
        {
            ShowError("No se pudo exportar GameHours", exception);
            StatusTextBlock.Text = "No se pudo crear la exportación portable.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Directory.CreateDirectory(_coordinator.ExportsDirectory);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Importar datos portables de GameHours",
            Filter = "GameHours JSON (*.json)|*.json|Todos los archivos (*.*)|*.*",
            InitialDirectory = _coordinator.ExportsDirectory,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        SetBusy(true, "Analizando el JSON sin modificar la base de datos…");
        try
        {
            var preview = await _coordinator.AnalyzePortableImportAsync(dialog.FileName);
            if (!preview.CanImport)
            {
                StatusTextBlock.Text =
                    $"Importación bloqueada · {preview.ConflictCount} conflicto(s) · no se modificó ningún dato.";
                System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    BuildPreviewText(preview, includeConflicts: true),
                    "El JSON no se puede importar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            var confirmation = System.Windows.MessageBox.Show(
                Window.GetWindow(this),
                BuildPreviewText(preview, includeConflicts: false) +
                "\n\nAntes de escribir, GameHours guardará cualquier sesión activa y volverá a validar todo el archivo dentro de la transacción.\n\n¿Importar estos datos?",
                "Previsualización de importación",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);
            if (confirmation != MessageBoxResult.Yes)
            {
                StatusTextBlock.Text = "Importación cancelada · no se modificó ningún dato.";
                return;
            }

            var restartTracker = _host.CurrentStatus.IsTracking;
            var trackerStopped = false;
            try
            {
                if (restartTracker)
                {
                    StatusTextBlock.Text = "Guardando cualquier sesión activa antes de importar…";
                    await _host.StopAsync();
                    trackerStopped = true;
                }

                StatusTextBlock.Text = "Revalidando e importando dentro de una transacción SQLite…";
                var result = await _coordinator.ImportPortableJsonAsync(dialog.FileName);
                await _host.RefreshLibraryAsync();
                StatusTextBlock.Text = BuildSuccessText(result.Preview);
            }
            catch (GameHoursPortableImportConflictException conflict)
            {
                StatusTextBlock.Text =
                    $"Importación bloqueada al revalidar · {conflict.Preview.ConflictCount} conflicto(s) · no se modificó ningún dato.";
                System.Windows.MessageBox.Show(
                    Window.GetWindow(this),
                    BuildPreviewText(conflict.Preview, includeConflicts: true),
                    "Los datos cambiaron antes de importar",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
            }
            finally
            {
                if (trackerStopped)
                {
                    try
                    {
                        await _host.StartAsync();
                    }
                    catch (Exception restartException)
                    {
                        ShowError("Los datos se procesaron, pero no se pudo reanudar el tracker", restartException);
                    }
                }
            }
        }
        catch (Exception exception)
        {
            ShowError("No se pudo importar el JSON de GameHours", exception);
            StatusTextBlock.Text = "No se pudo completar la importación.";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;

        Directory.CreateDirectory(_coordinator.BackupsDirectory);
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Restaurar una copia completa de GameHours",
            Filter = "Base de datos SQLite (*.db)|*.db|Todos los archivos (*.*)|*.*",
            InitialDirectory = _coordinator.BackupsDirectory,
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        var owner = Window.GetWindow(this);
        var confirmation = System.Windows.MessageBox.Show(
            owner,
            "GameHours sustituirá todos los datos locales por los de la copia seleccionada.\n\n" +
            "Antes guardará cualquier sesión activa y creará automáticamente una copia de seguridad " +
            "de la base actual. Después se reiniciará GameHours.\n\n¿Continuar?",
            "Restaurar GameHours",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes) return;

        if (System.Windows.Application.Current is not App app)
        {
            ShowError(
                "No se pudo iniciar la restauración",
                new InvalidOperationException("No hay un coordinador de aplicación disponible."));
            return;
        }

        SetBusy(true, "Guardando la sesión activa y preparando la restauración…");
        await app.RestoreBackupAndRestartAsync(dialog.FileName);
    }

    private void SetBusy(bool busy, string? status = null)
    {
        _busy = busy;
        BackupButton.IsEnabled = !busy;
        ExportButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy;
        RestoreButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusTextBlock.Text = status;
        }
    }

    private static string BuildPreviewText(
        GameHoursPortableImportPreview preview,
        bool includeConflicts)
    {
        var text = new StringBuilder();
        text.AppendLine($"Formato portable: v{preview.FormatVersion}");
        text.AppendLine($"Origen: {preview.SourceGameCount} juegos · {preview.SourceSessionCount} sesiones · {preview.SourceHistoricalEvidenceCount} históricos · {preview.SourceAchievementCount} logros · {preview.SourceAchievementEvidenceCount} evidencias de logro");
        text.AppendLine();
        text.AppendLine("Cambios previstos:");
        text.AppendLine($"  Juegos: +{preview.NewGameCount} · {preview.UpdatedGameCount} actualizados");
        text.AppendLine($"  Sesiones: +{preview.NewSessionCount} · {preview.DuplicateSessionCount} ya existentes");
        text.AppendLine($"  Histórico: +{preview.NewHistoricalEvidenceCount} · {preview.DuplicateHistoricalEvidenceCount} ya existente");
        text.AppendLine($"  Logros: +{preview.NewAchievementCount} · {preview.UpdatedAchievementCount} actualizados");
        text.AppendLine($"  Evidencias de logro: +{preview.NewAchievementEvidenceCount} · {preview.UpdatedAchievementEvidenceCount} actualizadas");
        text.AppendLine($"  Conflictos: {preview.ConflictCount}");

        if (includeConflicts && preview.Conflicts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Primeros conflictos:");
            foreach (var conflict in preview.Conflicts.Take(8))
            {
                text.AppendLine($"  • {conflict.EntityType} · {conflict.Code}: {conflict.Message}");
            }
            if (preview.Conflicts.Count > 8)
            {
                text.AppendLine($"  … y {preview.Conflicts.Count - 8} más.");
            }
            text.AppendLine();
            text.Append("No se ha modificado ningún dato.");
        }

        return text.ToString().TrimEnd();
    }

    private static string BuildSuccessText(GameHoursPortableImportPreview preview) =>
        $"Importación completada · +{preview.NewGameCount} juegos · +{preview.NewSessionCount} sesiones · " +
        $"+{preview.NewHistoricalEvidenceCount} históricos · +{preview.NewAchievementCount} logros · " +
        $"+{preview.NewAchievementEvidenceCount} evidencias de logro · " +
        $"{preview.DuplicateSessionCount + preview.DuplicateHistoricalEvidenceCount} duplicados ignorados.";

    private void ShowError(string title, Exception exception) =>
        System.Windows.MessageBox.Show(
            Window.GetWindow(this),
            exception.Message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L)
        {
            return $"{bytes / (1024d * 1024d):0.0} MiB";
        }
        if (bytes >= 1024L)
        {
            return $"{bytes / 1024d:0.0} KiB";
        }
        return $"{bytes:N0} bytes";
    }
}
