using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using GameHours.Core.Discovery;
using GameHours.Storage.Sqlite;
using GameHours.Windows.Discovery;
using WpfMessageBox = System.Windows.MessageBox;
using WpfOpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace GameHours.Desktop;

public partial class CandidateCenterWindow : Window, INotifyPropertyChanged
{
    private readonly GameHoursDatabase _database;
    private readonly SqliteGameCandidateRepository _candidates;
    private readonly SqliteGameRepository _games;
    private readonly SqliteExecutableMappingRepository _mappings;
    private readonly ManualGameRegistrationService _manualRegistration;
    private readonly CandidateDecisionService _decisions;
    private CandidateItemViewModel? _selectedCandidate;
    private ExistingGameChoice? _selectedExistingGame;
    private RoleChoice? _selectedHelperRole;
    private string _statusText = "Cargando candidatos…";
    private bool _busy;

    public ObservableCollection<CandidateItemViewModel> Candidates { get; } = new();
    public ObservableCollection<ExistingGameChoice> ExistingGames { get; } = new();
    public ObservableCollection<RoleChoice> HelperRoleChoices { get; } = new()
    {
        new(ExecutableRole.Launcher, "Launcher"),
        new(ExecutableRole.Helper, "Helper / proceso auxiliar"),
        new(ExecutableRole.AntiCheat, "Anti-cheat"),
        new(ExecutableRole.Updater, "Actualizador / patcher"),
        new(ExecutableRole.CrashHandler, "Crash reporter")
    };

    public CandidateItemViewModel? SelectedCandidate
    {
        get => _selectedCandidate;
        set
        {
            if (SetField(ref _selectedCandidate, value))
            {
                OnPropertyChanged(nameof(HasSelection));
            }
        }
    }

    public ExistingGameChoice? SelectedExistingGame
    {
        get => _selectedExistingGame;
        set => SetField(ref _selectedExistingGame, value);
    }

    public RoleChoice? SelectedHelperRole
    {
        get => _selectedHelperRole;
        set => SetField(ref _selectedHelperRole, value);
    }

    public bool HasSelection => SelectedCandidate is not null && !_busy;

    public string StatusText
    {
        get => _statusText;
        private set => SetField(ref _statusText, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? CandidateResolved;

    public CandidateCenterWindow(string databasePath)
    {
        _database = new GameHoursDatabase(databasePath);
        _candidates = new SqliteGameCandidateRepository(_database);
        _games = new SqliteGameRepository(_database);
        _mappings = new SqliteExecutableMappingRepository(_database);
        _manualRegistration = new ManualGameRegistrationService(_games, _mappings);
        _decisions = new CandidateDecisionService(_candidates, _mappings, new LocalExecutableRoleOverrideStore());
        _selectedHelperRole = HelperRoleChoices[0];

        InitializeComponent();
        DataContext = this;
        Loaded += Window_Loaded;
    }

    public void RequestRefresh()
    {
        if (!IsLoaded || _busy)
        {
            return;
        }

        _ = ReloadAsync();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= Window_Loaded;
        await _database.InitializeAsync();
        await _candidates.InitializeAsync();
        await ReloadAsync();
    }

    private async Task ReloadAsync(string? preferredPath = null)
    {
        SetBusy(true);
        try
        {
            preferredPath ??= SelectedCandidate?.ExecutablePath;
            var pendingTask = _candidates.GetPendingAsync();
            var gamesTask = _games.GetAllAsync();
            await Task.WhenAll(pendingTask, gamesTask);

            var pending = await pendingTask;
            var games = await gamesTask;

            Candidates.Clear();
            foreach (var candidate in pending)
            {
                Candidates.Add(new CandidateItemViewModel(candidate));
            }

            ExistingGames.Clear();
            foreach (var game in games.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
            {
                ExistingGames.Add(new ExistingGameChoice(game.Id, game.Title));
            }

            SelectedCandidate = preferredPath is null
                ? Candidates.FirstOrDefault()
                : Candidates.FirstOrDefault(item =>
                    string.Equals(item.ExecutablePath, preferredPath, StringComparison.OrdinalIgnoreCase))
                  ?? Candidates.FirstOrDefault();

            if (SelectedExistingGame is { } selectedGame)
            {
                SelectedExistingGame = ExistingGames.FirstOrDefault(item => item.GameId == selectedGame.GameId);
            }

            StatusText = Candidates.Count switch
            {
                0 => "No hay ejecutables pendientes de identificar.",
                1 => "1 ejecutable pendiente. Ninguno cuenta horas hasta ser confirmado.",
                _ => $"{Candidates.Count} ejecutables pendientes. Ninguno cuenta horas hasta ser confirmado."
            };
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = $"No se pudieron cargar los candidatos: {exception.Message}";
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void CreateGame_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is not { } candidate || _busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(candidate.ProposedTitle))
        {
            WpfMessageBox.Show(this, "Escribe un nombre para el juego.", "GameHours", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ExecuteDecisionAsync(async () =>
        {
            var game = await _manualRegistration.RegisterAsync(candidate.ExecutablePath, candidate.ProposedTitle);
            await _decisions.ConfirmGameAsync(candidate.ExecutablePath, game.Id, ExecutableRole.PrimaryGame);
        });
    }

    private async void AssociateGame_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is not { } candidate || SelectedExistingGame is not { } game || _busy)
        {
            if (SelectedCandidate is not null)
            {
                WpfMessageBox.Show(this, "Selecciona primero un juego existente.", "GameHours", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            return;
        }

        await ExecuteDecisionAsync(() =>
            _decisions.ConfirmGameAsync(candidate.ExecutablePath, game.GameId, ExecutableRole.SecondaryGame));
    }

    private async void SaveHelperRole_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is not { } candidate || SelectedHelperRole is not { } roleChoice || _busy)
        {
            return;
        }

        await ExecuteDecisionAsync(() =>
            _decisions.ClassifyHelperAsync(
                candidate.ExecutablePath,
                roleChoice.Role,
                SelectedExistingGame?.GameId));
    }

    private async void Ignore_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedCandidate is not { } candidate || _busy)
        {
            return;
        }

        await ExecuteDecisionAsync(() => _decisions.IgnoreAsync(candidate.ExecutablePath));
    }

    private async void AddExecutable_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        var dialog = new WpfOpenFileDialog
        {
            Title = "Seleccionar ejecutable del juego",
            Filter = "Ejecutables (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var path = Path.GetFullPath(dialog.FileName);
        await _candidates.ObserveAsync(
            new GameCandidateObservation(
                path,
                Path.GetFileNameWithoutExtension(path),
                Path.GetFileNameWithoutExtension(path),
                0.50,
                "manual_candidate",
                ExecutableRole.Unknown,
                Array.Empty<GameDetectionEvidence>(),
                DateTimeOffset.UtcNow));
        await ReloadAsync(path);
    }

    private async Task ExecuteDecisionAsync(Func<Task> action)
    {
        var selectedPath = SelectedCandidate?.ExecutablePath;
        SetBusy(true);
        try
        {
            await action();
            CandidateResolved?.Invoke();
            await ReloadAsync(selectedPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            WpfMessageBox.Show(
                this,
                exception.Message,
                "No se pudo guardar la decisión",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ReloadAsync();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void SetBusy(bool busy)
    {
        if (_busy == busy)
        {
            return;
        }

        _busy = busy;
        OnPropertyChanged(nameof(HasSelection));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class CandidateItemViewModel : INotifyPropertyChanged
{
    private string _proposedTitle;

    public string ExecutablePath { get; }
    public string ExecutableName { get; }
    public string SuggestedTitle { get; }
    public string ConfidenceText { get; }
    public string MethodText { get; }
    public string LastSeenText { get; }
    public string ObservationText { get; }
    public IReadOnlyList<string> EvidenceLines { get; }

    public string ProposedTitle
    {
        get => _proposedTitle;
        set
        {
            var normalized = value ?? string.Empty;
            if (string.Equals(_proposedTitle, normalized, StringComparison.Ordinal))
            {
                return;
            }

            _proposedTitle = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProposedTitle)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public CandidateItemViewModel(GameCandidate candidate)
    {
        ExecutablePath = candidate.ExecutablePath;
        ExecutableName = candidate.ExecutableName;
        SuggestedTitle = candidate.SuggestedTitle;
        _proposedTitle = candidate.SuggestedTitle;
        ConfidenceText = $"{candidate.Confidence:P0}";
        MethodText = candidate.Method.Replace('_', ' ');
        LastSeenText = $"Visto {candidate.LastSeenAtUtc.ToLocalTime():g}";
        ObservationText = candidate.ObservationCount == 1
            ? "1 vez"
            : $"{candidate.ObservationCount} veces";
        EvidenceLines = candidate.Evidence.Count == 0
            ? new[] { "• Añadido manualmente; sin heurísticas automáticas." }
            : candidate.Evidence
                .OrderByDescending(item => Math.Abs(item.Weight))
                .Select(item =>
                {
                    var sign = item.Weight > 0 ? "+" : item.Weight < 0 ? "−" : "·";
                    return $"{sign} {FormatEvidenceKind(item.Kind)} · {item.Detail}";
                })
                .ToArray();
    }

    private static string FormatEvidenceKind(GameDetectionEvidenceKind kind) => kind switch
    {
        GameDetectionEvidenceKind.InstalledGamePath => "carpeta de instalación",
        GameDetectionEvidenceKind.LearnedExecutablePath => "ruta aprendida",
        GameDetectionEvidenceKind.WindowsGameConfigStore => "Windows GameConfigStore",
        GameDetectionEvidenceKind.UnrealRuntime => "Unreal Engine",
        GameDetectionEvidenceKind.UnityRuntime => "Unity",
        GameDetectionEvidenceKind.GraphicsRuntime => "runtime gráfico",
        GameDetectionEvidenceKind.VisibleWindow => "ventana visible",
        GameDetectionEvidenceKind.ForegroundWindow => "ventana en primer plano",
        GameDetectionEvidenceKind.FilenameHeuristic => "nombre/ruta",
        GameDetectionEvidenceKind.ProcessRelationship => "relación de procesos",
        GameDetectionEvidenceKind.ProcessRelationshipHistory => "relación reciente de procesos",
        GameDetectionEvidenceKind.ExecutableRole => "rol del ejecutable",
        _ => kind.ToString()
    };
}

public sealed record ExistingGameChoice(Guid GameId, string Title);
public sealed record RoleChoice(ExecutableRole Role, string DisplayName);
