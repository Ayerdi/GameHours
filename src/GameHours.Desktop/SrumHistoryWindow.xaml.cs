using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GameHours.Core.Domain;

namespace GameHours.Desktop;

public partial class SrumHistoryWindow : Window, INotifyPropertyChanged
{
    private readonly DesktopSrumHistoryService _service;
    private bool _busy;
    private string _statusText = "Preparando análisis…";
    private string _sourceText = string.Empty;

    public ObservableCollection<SrumCandidateViewModel> Candidates { get; } = new();

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public string SourceText
    {
        get => _sourceText;
        private set => SetField(ref _sourceText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SrumHistoryWindow(string databasePath)
    {
        _service = new DesktopSrumHistoryService(databasePath);
        InitializeComponent();
        DataContext = this;
        Loaded += Window_Loaded;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await LoadPreviewAsync();
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        await LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        StatusText = "Analizando historial local de Windows…";
        SourceText = "Solo lectura · ninguna evidencia se importará sin tu confirmación.";

        try
        {
            var preview = await Task.Run(() => _service.PreviewAsync());
            Candidates.Clear();
            foreach (var candidate in preview.Candidates)
            {
                Candidates.Add(new SrumCandidateViewModel(candidate));
            }

            var selectableCount = Candidates.Count(candidate => candidate.CanSelect);
            var gapCount = Candidates.Count(candidate =>
                candidate.CanSelect && candidate.Candidate.Kind is EvidenceKind.GapRecovery);

            StatusText = Candidates.Count switch
            {
                0 => "No se ha encontrado historial o actividad recuperable que GameHours pueda asociar con seguridad a tus juegos.",
                _ when selectableCount == 0 =>
                    $"Se han encontrado {Candidates.Count} elementos y todos estaban ya importados.",
                _ when gapCount == 1 =>
                    $"Se han encontrado {Candidates.Count} elementos · 1 hueco de actividad recuperable.",
                _ when gapCount > 1 =>
                    $"Se han encontrado {Candidates.Count} elementos · {gapCount} huecos de actividad recuperable.",
                1 => "Se ha encontrado 1 elemento pendiente de importar.",
                _ => $"Se han encontrado {Candidates.Count} elementos · {selectableCount} pendientes de importar."
            };

            SourceText =
                $"SRUM · {preview.BaselineRowCount} registros previos · {preview.GapRowCount} posteriores revisados · corte {FormatDate(preview.TrackingStartedAtUtc)}. " +
                "Los huecos posteriores se descartan si pueden solapar sesiones o evidencia ya conservada.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileNotFoundException or InvalidOperationException or
            UnauthorizedAccessException or IOException or OverflowException)
        {
            Candidates.Clear();
            StatusText = "No se pudo analizar el historial de Windows.";
            SourceText = exception.Message;
        }
        finally
        {
            SetBusy(false);
            UpdateImportButton();
        }
    }

    private async void ImportSelected_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var selectedRows = Candidates
            .Where(candidate => candidate.IsSelected && candidate.CanSelect)
            .ToArray();
        if (selectedRows.Length == 0)
        {
            return;
        }

        SetBusy(true);
        StatusText = selectedRows.Length == 1
            ? "Importando 1 elemento de evidencia…"
            : $"Importando {selectedRows.Length} elementos de evidencia…";

        try
        {
            var selectedCandidates = selectedRows
                .Select(row => row.Candidate)
                .ToArray();
            var result = await Task.Run(() => _service.ImportAsync(selectedCandidates));

            foreach (var row in selectedRows)
            {
                if (result.CompletedCandidateKeys.Contains(row.Candidate.CandidateKey))
                {
                    row.MarkImported();
                }
            }

            StatusText = (result.AddedCount, result.SkippedCount) switch
            {
                (0, 0) => "No había nada nuevo que importar.",
                (1, 0) => "Se ha importado 1 bloque de evidencia estimada.",
                (_, 0) => $"Se han importado {result.AddedCount} bloques de evidencia estimada.",
                _ => $"Importados {result.AddedCount} bloques · {result.SkippedCount} descartados al revalidar solapes."
            };
            SourceText =
                "La biblioteca reflejará el tiempo recuperado al actualizarse. Las sesiones medidas siguen siendo la fuente autoritativa.";
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or UnauthorizedAccessException or
            IOException or OverflowException)
        {
            StatusText = "No se pudo importar la evidencia seleccionada.";
            SourceText = exception.Message;
        }
        finally
        {
            SetBusy(false);
            UpdateImportButton();
        }
    }

    private void CandidateSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateImportButton();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        AnalyzeButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && Candidates.Any(candidate => candidate.IsSelected && candidate.CanSelect);
    }

    private void UpdateImportButton()
    {
        ImportButton.IsEnabled = !_busy && Candidates.Any(candidate => candidate.IsSelected && candidate.CanSelect);
    }

    private static string FormatDate(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("dd MMM · HH:mm")
            : local.ToString("dd/MM/yy · HH:mm");
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public sealed class SrumCandidateViewModel : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _alreadyImported;

        internal DesktopSrumHistoryCandidate Candidate { get; }

        public string GameTitle => Candidate.GameTitle;
        public string PlaytimeText => FormatDuration(Candidate.KnownPlaytime);
        public string CoverageText =>
            $"{FormatCompactDate(Candidate.FirstRecordedAtUtc)} – {FormatCompactDate(Candidate.LastRecordedAtUtc)}";
        public string ApplicationText
        {
            get
            {
                var prefix = Candidate.Kind is EvidenceKind.GapRecovery
                    ? "Hueco posterior"
                    : "Histórico previo";
                return Candidate.Applications.Count switch
                {
                    0 => $"{prefix} · sin ejecutable conservado",
                    1 => $"{prefix} · {Candidate.Applications[0]}",
                    _ => $"{prefix} · {Candidate.Applications[0]} · +{Candidate.Applications.Count - 1} ejecutables"
                };
            }
        }
        public string StateText => Candidate.Kind switch
        {
            EvidenceKind.GapRecovery when _alreadyImported => "Recuperado",
            EvidenceKind.GapRecovery => "Recuperable",
            _ when _alreadyImported => "Importado",
            _ => "Pendiente"
        };
        public bool CanSelect => !_alreadyImported;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                var normalized = CanSelect && value;
                if (_isSelected == normalized)
                {
                    return;
                }

                _isSelected = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        internal SrumCandidateViewModel(DesktopSrumHistoryCandidate candidate)
        {
            Candidate = candidate;
            _alreadyImported = candidate.AlreadyImported;
            _isSelected = !candidate.AlreadyImported;
        }

        internal void MarkImported()
        {
            _alreadyImported = true;
            _isSelected = false;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StateText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanSelect)));
        }

        private static string FormatDuration(TimeSpan duration)
        {
            if (duration.TotalHours >= 100)
            {
                return $"{duration.TotalHours:0} h";
            }

            if (duration.TotalHours >= 10)
            {
                return $"{duration.TotalHours:0.0} h";
            }

            if (duration.TotalHours >= 1)
            {
                return $"{duration.TotalHours:0.00} h";
            }

            return $"{Math.Max(0, duration.TotalMinutes):0} min";
        }

        private static string FormatCompactDate(DateTimeOffset value)
        {
            var local = value.ToLocalTime();
            return local.Year == DateTimeOffset.Now.Year
                ? local.ToString("dd MMM")
                : local.ToString("dd/MM/yy");
        }
    }
}
