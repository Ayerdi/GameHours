using System.Windows;

namespace GameHours.Desktop;

public partial class DataPortabilitySettingsCard : System.Windows.Controls.UserControl
{
    private readonly DesktopDataPortabilityCoordinator _coordinator;
    private bool _busy;

    public DataPortabilitySettingsCard(string databasePath)
    {
        _coordinator = new DesktopDataPortabilityCoordinator(databasePath);
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
        RestoreButton.IsEnabled = !busy;
        if (!string.IsNullOrWhiteSpace(status))
        {
            StatusTextBlock.Text = status;
        }
    }

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
