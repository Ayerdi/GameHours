using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameHours.Windows.Achievements;

namespace GameHours.Desktop;

public partial class GameDetailView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly ILocalAchievementProvider _achievementProvider = new AggregatingLocalAchievementProvider();
    private readonly LocalAchievementSupportInspector _achievementSupportInspector = new();
    private readonly SteamAchievementMetadataCache _steamAchievementMetadataCache = new();
    private readonly GseAchievementCatalogueProvisioner _gseAchievementCatalogueProvisioner = new();
    private readonly HashSet<string> _achievementPreparationInFlight = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _databasePath;
    private readonly DesktopGameInsightService _insightService;
    private readonly DispatcherTimer _achievementRefreshTimer;
    private FileSystemWatcher? _achievementWatcher;
    private Guid? _currentGameId;
    private string? _currentExecutablePath;
    private string? _gseAchievementPreparationPath;
    private string? _activityTelemetryText;
    private bool _hasLiveAchievementSnapshot;
    private bool _achievementTimingEvidenceLoaded;
    private HashSet<string> _unverifiedHistoricalAchievementTimes = new(StringComparer.OrdinalIgnoreCase);
    private string _achievementCountText = "—";
    private string _achievementSourceText = "Sin fuente local compatible";
    private string _achievementStatusText = "GameHours todavía no ha detectado logros locales para este juego.";
    private string _historicalSourceText = "Sin histórico recuperado";
    private string _historicalCoverageText = "No hay una ventana de evidencia histórica guardada.";
    private string _firstAchievementText = "—";
    private string _lastAchievementText = "—";
    private string _achievementProgressText = "Sin datos persistidos";
    private string _activitySummaryText = "Cargando actividad persistida…";
    private string _focusedTotalText = "—";
    private string _activeTotalText = "—";
    private string _afkTotalText = "—";
    private string _attentionCoverageText = "Sin telemetría de atención persistida.";

    public event EventHandler? BackRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AchievementRowViewModel> AchievementRows { get; } = new();
    public ObservableCollection<MainWindow.ActivityRowViewModel> RecentActivity { get; } = new();

    public string AchievementCountText { get => _achievementCountText; private set => SetField(ref _achievementCountText, value); }
    public string AchievementSourceText { get => _achievementSourceText; private set => SetField(ref _achievementSourceText, value); }
    public string AchievementStatusText { get => _achievementStatusText; private set => SetField(ref _achievementStatusText, value); }
    public string HistoricalSourceText { get => _historicalSourceText; private set => SetField(ref _historicalSourceText, value); }
    public string HistoricalCoverageText { get => _historicalCoverageText; private set => SetField(ref _historicalCoverageText, value); }
    public string FirstAchievementText { get => _firstAchievementText; private set => SetField(ref _firstAchievementText, value); }
    public string LastAchievementText { get => _lastAchievementText; private set => SetField(ref _lastAchievementText, value); }
    public string AchievementProgressText { get => _achievementProgressText; private set => SetField(ref _achievementProgressText, value); }
    public string ActivitySummaryText { get => _activitySummaryText; private set => SetField(ref _activitySummaryText, value); }
    public string FocusedTotalText { get => _focusedTotalText; private set => SetField(ref _focusedTotalText, value); }
    public string ActiveTotalText { get => _activeTotalText; private set => SetField(ref _activeTotalText, value); }
    public string AfkTotalText { get => _afkTotalText; private set => SetField(ref _afkTotalText, value); }
    public string AttentionCoverageText { get => _attentionCoverageText; private set => SetField(ref _attentionCoverageText, value); }

    public GameDetailView()
    {
        _databasePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours",
            "gamehours.db");
        _insightService = new DesktopGameInsightService(_databasePath);

        InitializeComponent();
        DataContextChanged += GameDetailView_DataContextChanged;
        AddHandler(
            PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(SessionRow_PreviewMouseLeftButtonUp));

        _achievementRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _achievementRefreshTimer.Tick += (_, _) =>
        {
            _achievementRefreshTimer.Stop();
            LoadAchievements(_currentExecutablePath);
            if (_currentGameId is Guid gameId) _ = LoadPersistedInsightsAsync(gameId);
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e) => BackRequested?.Invoke(this, EventArgs.Empty);

    private async void RefreshAchievements_Click(object sender, RoutedEventArgs e)
    {
        var executablePath = _currentExecutablePath;
        if (!string.IsNullOrWhiteSpace(executablePath) &&
            string.Equals(_gseAchievementPreparationPath, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            var confirmation = System.Windows.MessageBox.Show(
                "GameHours puede crear steam_settings\\achievements.json dentro de la instalación de este juego usando únicamente los identificadores públicos de logros de Steam.\n\n" +
                "Esto permite que GSE/Goldberg registre futuros desbloqueos. No modifica el estado de logros del usuario ni puede recuperar desbloqueos históricos que el emulador nunca guardó.\n\n" +
                "¿Quieres preparar los logros para este juego?",
                "Preparar logros GSE/Goldberg",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Information,
                MessageBoxResult.Cancel);

            if (confirmation == MessageBoxResult.OK)
            {
                await TryPrepareGseAchievementCatalogueAsync(executablePath, _currentGameId);
            }

            return;
        }

        LoadAchievements(executablePath);
        if (_currentGameId is Guid gameId) _ = LoadPersistedInsightsAsync(gameId);
    }

    private void SessionRow_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (SessionDetailNavigation.TryOpenFromVisual(
                e.OriginalSource as DependencyObject,
                _databasePath,
                Window.GetWindow(this)))
        {
            e.Handled = true;
        }
    }

    private void GameDetailView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _hasLiveAchievementSnapshot = false;
        _achievementTimingEvidenceLoaded = false;
        _unverifiedHistoricalAchievementTimes.Clear();
        RecentActivity.Clear();
        _activityTelemetryText = null;
        ActivitySummaryText = "Cargando actividad persistida…";
        FocusedTotalText = "—";
        ActiveTotalText = "—";
        AfkTotalText = "—";
        AttentionCoverageText = "Sin telemetría de atención persistida.";

        if (e.NewValue is MainWindow.GameDetailViewModel detail)
        {
            _currentGameId = detail.GameId;
            _currentExecutablePath = !string.Equals(
                detail.ExecutableText,
                "Sin ejecutable asociado",
                StringComparison.Ordinal)
                ? detail.ExecutableText
                : null;
            _activityTelemetryText = detail.FocusedText == "—"
                ? detail.ActivityCoverageText
                : detail.ActiveText == "—"
                    ? $"En primer plano {detail.FocusedText}. Activo estimado no disponible para sesiones sin filtro AFK. {detail.ActivityCoverageText}"
                    : $"En primer plano {detail.FocusedText} · activo estimado {detail.ActiveText}. {detail.ActivityCoverageText}";
            FocusedTotalText = detail.FocusedText;
            ActiveTotalText = detail.ActiveText;
            ActivitySummaryText = _activityTelemetryText;
            _ = LoadPersistedInsightsAsync(detail.GameId);
        }
        else
        {
            _currentGameId = null;
            _currentExecutablePath = null;
            ResetInsights();
        }

        LoadAchievements(_currentExecutablePath);
    }

    private async Task LoadPersistedInsightsAsync(Guid gameId)
    {
        try
        {
            var insight = await _insightService.LoadAsync(gameId);
            if (_currentGameId != gameId || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            await Dispatcher.InvokeAsync(() =>
            {
                if (_currentGameId != gameId) return;

                var timingChanged = !_achievementTimingEvidenceLoaded ||
                    !_unverifiedHistoricalAchievementTimes.SetEquals(
                        insight.UnverifiedHistoricalAchievementApiNames);
                _achievementTimingEvidenceLoaded = true;
                _unverifiedHistoricalAchievementTimes = insight.UnverifiedHistoricalAchievementApiNames
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (timingChanged && !string.IsNullOrWhiteSpace(_currentExecutablePath))
                {
                    LoadAchievements(_currentExecutablePath, refreshMetadata: false);
                }

                HistoricalSourceText = insight.HistoricalSourceText;
                HistoricalCoverageText = insight.HistoricalCoverageText;
                ActivitySummaryText = CombineActivitySummary(insight.ActivitySummaryText, _activityTelemetryText);
                FocusedTotalText = FormatAttentionDuration(insight.FocusedPlaytime);
                ActiveTotalText = FormatAttentionDuration(insight.ActivePlaytime);
                AfkTotalText = FormatAttentionDuration(insight.AfkPlaytime);
                AttentionCoverageText = BuildAttentionCoverageText(insight);
                FirstAchievementText = insight.FirstAchievementText;
                LastAchievementText = insight.LastAchievementText;
                if (!_hasLiveAchievementSnapshot)
                {
                    AchievementProgressText = insight.AchievementProgressText;
                }

                RecentActivity.Clear();
                foreach (var activity in insight.RecentActivity)
                {
                    var row = new MainWindow.ActivityRowViewModel(activity);
                    SessionDetailNavigation.Register(row, activity.SessionId);
                    RecentActivity.Add(row);
                }
            });
        }
        catch
        {
            // Insight enrichment is optional and must never block the game-detail view.
            if (_currentGameId == gameId && !Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    if (_currentGameId == gameId)
                    {
                        ActivitySummaryText = string.IsNullOrWhiteSpace(_activityTelemetryText)
                            ? "No se pudo cargar la actividad persistida de este juego."
                            : _activityTelemetryText;
                    }
                });
            }
        }
    }

    private static string BuildAttentionCoverageText(DesktopGameInsight insight)
    {
        if (insight.ActivitySessionCount == 0)
        {
            return "Las sesiones antiguas sin telemetría no se convierten en ceros artificiales.";
        }

        var focus = insight.ActivitySessionCount == insight.MeasuredSessionCount
            ? $"Foco medido en las {insight.ActivitySessionCount} sesiones."
            : $"Foco medido en {insight.ActivitySessionCount} de {insight.MeasuredSessionCount} sesiones.";
        var afk = insight.AfkEstimatedSessionCount == 0
            ? "Ninguna sesión de este conjunto usó estimación AFK."
            : $"Activo y AFK calculados en {insight.AfkEstimatedSessionCount} sesiones con filtro AFK.";
        return $"{focus} {afk}";
    }

    private static string FormatAttentionDuration(TimeSpan? value)
    {
        if (value is not TimeSpan duration) return "—";
        if (duration < TimeSpan.FromSeconds(1)) return duration <= TimeSpan.Zero ? "0 s" : "<1 s";
        if (duration < TimeSpan.FromMinutes(1)) return $"{Math.Max(1, (int)Math.Round(duration.TotalSeconds))} s";
        var totalMinutes = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        var hours = totalMinutes / 60;
        var minutes = totalMinutes % 60;
        if (hours == 0) return $"{minutes} min";
        return minutes == 0 ? $"{hours} h" : $"{hours} h {minutes} min";
    }

    private static string CombineActivitySummary(string persisted, string? telemetry)
    {
        if (string.IsNullOrWhiteSpace(telemetry)) return persisted;
        if (string.IsNullOrWhiteSpace(persisted)) return telemetry;
        return $"{persisted} {telemetry}";
    }

    private void LoadAchievements(string? executablePath, bool refreshMetadata = true)
    {
        AchievementRows.Clear();
        _hasLiveAchievementSnapshot = false;
        _gseAchievementPreparationPath = null;

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            StopAchievementWatcher();
            SetUnavailable("Sin ejecutable asociado; no se puede buscar una fuente local de logros.");
            return;
        }

        var snapshot = _achievementProvider.TryRead(executablePath);
        if (snapshot is null)
        {
            StopAchievementWatcher();
            var hint = _achievementSupportInspector.Inspect(executablePath);
            if (hint is null)
            {
                SetUnavailable("No se ha detectado ninguna fuente local de logros compatible para este juego.");
            }
            else
            {
                _gseAchievementPreparationPath = executablePath;
                SetUnavailable(
                    "Esta instalación usa GSE/Goldberg pero no tiene un catálogo de logros ni estado local de desbloqueos. Si quieres que GameHours prepare las definiciones necesarias para futuros desbloqueos, pulsa «Actualizar logros»; antes de escribir nada te pedirá confirmación.",
                    hint.SourceText);
            }
            return;
        }

        _hasLiveAchievementSnapshot = true;
        ConfigureAchievementWatcher(snapshot.StatePath);

        var total = snapshot.Achievements.Count;
        var unlocked = snapshot.UnlockedCount;
        var partialState = !snapshot.IsCatalogueComplete;
        var isGseSource = snapshot.Source.Contains("GSE/Goldberg", StringComparison.OrdinalIgnoreCase);
        var suppressGseUnlockTimes = isGseSource && !_achievementTimingEvidenceLoaded;
        var canPrepareMissingGseCatalogue = partialState && isGseSource;
        if (canPrepareMissingGseCatalogue)
        {
            _gseAchievementPreparationPath = executablePath;
        }

        AchievementCountText = partialState ? $"{unlocked} desbloq." : $"{unlocked}/{total}";
        AchievementSourceText = string.IsNullOrWhiteSpace(snapshot.AppId)
            ? snapshot.Source
            : $"{snapshot.Source} · AppID {snapshot.AppId}";

        if (partialState)
        {
            var partialStatus = unlocked == 1
                ? "1 logro desbloqueado detectado localmente · el catálogo completo no está disponible en esta fuente."
                : $"{unlocked} logros desbloqueados detectados localmente · el catálogo completo no está disponible en esta fuente.";
            AchievementStatusText = canPrepareMissingGseCatalogue
                ? $"{partialStatus} Pulsa «Actualizar logros» si quieres preparar el catálogo GSE/Goldberg para futuros desbloqueos; antes de escribir nada te pedirá confirmación."
                : partialStatus;
        }
        else if (snapshot.StatePath is null)
        {
            AchievementStatusText = "Se encontraron las definiciones, pero todavía no existe un estado local de logros del usuario.";
        }
        else
        {
            var percentage = total == 0 ? 0d : unlocked * 100d / total;
            AchievementStatusText = $"{percentage:0}% completado · estado de desbloqueo leído localmente.";
        }

        UpdateLiveAchievementInsights(snapshot, suppressGseUnlockTimes);

        foreach (var achievement in snapshot.Achievements
                     .OrderByDescending(item => item.IsUnlocked)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            AchievementRows.Add(new AchievementRowViewModel(
                achievement,
                partialState,
                suppressUnlockTime: suppressGseUnlockTimes,
                historicalTimeUnverified: _unverifiedHistoricalAchievementTimes.Contains(achievement.ApiName)));
        }

        if (refreshMetadata && NeedsSteamMetadata(snapshot))
        {
            _ = RefreshSteamAchievementMetadataAsync(snapshot.AppId!, executablePath, _currentGameId);
        }
    }

    private async Task TryPrepareGseAchievementCatalogueAsync(string executablePath, Guid? gameId)
    {
        if (!_achievementPreparationInFlight.Add(executablePath))
        {
            return;
        }

        if (_currentGameId == gameId &&
            string.Equals(_currentExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
        {
            AchievementSourceText = "GSE/Goldberg detectado · preparando catálogo";
            AchievementStatusText = "GameHours está obteniendo los identificadores públicos de los logros para que el emulador pueda registrar futuros desbloqueos.";
        }

        try
        {
            var result = await Task.Run(
                () => _gseAchievementCatalogueProvisioner.TryProvisionAsync(executablePath));

            if (_currentGameId != gameId ||
                !string.Equals(_currentExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase) ||
                Dispatcher.HasShutdownStarted ||
                Dispatcher.HasShutdownFinished)
            {
                return;
            }

            switch (result.Status)
            {
                case GseAchievementCatalogueProvisioningStatus.Created:
                case GseAchievementCatalogueProvisioningStatus.AlreadyPresent:
                    LoadAchievements(executablePath);
                    if (result.Status == GseAchievementCatalogueProvisioningStatus.Created)
                    {
                        AchievementStatusText = result.AchievementCount == 1
                            ? "GameHours ha preparado 1 definición para GSE/Goldberg. El emulador podrá registrar ese logro a partir del próximo inicio del juego."
                            : $"GameHours ha preparado {result.AchievementCount} definiciones para GSE/Goldberg. El emulador podrá registrar esos logros a partir del próximo inicio del juego.";
                    }
                    break;

                case GseAchievementCatalogueProvisioningStatus.CatalogueUnavailable:
                    _gseAchievementPreparationPath = executablePath;
                    SetUnavailable(
                        "GSE/Goldberg está presente, pero Steam no ha devuelto ahora mismo un catálogo público utilizable. No se ha creado ningún fichero vacío; puedes reintentar con «Actualizar logros».",
                        "GSE/Goldberg detectado · catálogo no disponible");
                    break;

                case GseAchievementCatalogueProvisioningStatus.AppIdUnavailable:
                    _gseAchievementPreparationPath = null;
                    SetUnavailable(
                        "Se ha detectado GSE/Goldberg, pero no se ha podido resolver un AppID de Steam fiable para preparar sus logros.",
                        "GSE/Goldberg detectado · AppID desconocido");
                    break;

                case GseAchievementCatalogueProvisioningStatus.Failed:
                    _gseAchievementPreparationPath = executablePath;
                    SetUnavailable(
                        "Se ha detectado GSE/Goldberg, pero no se pudo preparar su catálogo local. No se ha sobrescrito ningún fichero existente; puedes reintentar con «Actualizar logros».",
                        "GSE/Goldberg detectado · preparación fallida");
                    break;

                case GseAchievementCatalogueProvisioningStatus.NotApplicable:
                    LoadAchievements(executablePath);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _achievementPreparationInFlight.Remove(executablePath);
        }
    }

    private async Task RefreshSteamAchievementMetadataAsync(
        string appId,
        string executablePath,
        Guid? gameId)
    {
        var updated = await _steamAchievementMetadataCache.EnsureFreshAsync(appId).ConfigureAwait(false);
        if (!updated || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

        try
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    if (_currentGameId != gameId ||
                        !string.Equals(_currentExecutablePath, executablePath, StringComparison.OrdinalIgnoreCase))
                    {
                        return;
                    }

                    LoadAchievements(executablePath, refreshMetadata: false);
                },
                DispatcherPriority.Background);
        }
        catch (Exception exception) when (exception is TaskCanceledException or InvalidOperationException)
        {
            // The view may be shutting down while an optional metadata refresh completes.
        }
    }

    private static bool NeedsSteamMetadata(LocalAchievementSnapshot snapshot) =>
        !string.IsNullOrWhiteSpace(snapshot.AppId) &&
        snapshot.AppId.All(char.IsDigit) &&
        (!snapshot.IsCatalogueComplete ||
         snapshot.Achievements.Count == 0 ||
         snapshot.Achievements.Any(achievement =>
             string.Equals(achievement.DisplayName, achievement.ApiName, StringComparison.OrdinalIgnoreCase) ||
             string.IsNullOrWhiteSpace(achievement.IconPath) ||
             string.IsNullOrWhiteSpace(achievement.LockedIconPath)));

    private void UpdateLiveAchievementInsights(
        LocalAchievementSnapshot snapshot,
        bool suppressGseUnlockTimes)
    {
        var unlocked = snapshot.Achievements.Where(achievement => achievement.IsUnlocked).ToArray();
        var datedUnlocks = unlocked
            .Where(achievement =>
                !suppressGseUnlockTimes &&
                !_unverifiedHistoricalAchievementTimes.Contains(achievement.ApiName) &&
                achievement.UnlockedAtUtc is not null)
            .Select(achievement => achievement.UnlockedAtUtc!.Value)
            .ToArray();

        FirstAchievementText = datedUnlocks.Length == 0
            ? unlocked.Length > 0 ? "Fecha histórica no disponible" : "—"
            : FormatInsightDate(datedUnlocks.Min());
        LastAchievementText = datedUnlocks.Length == 0
            ? unlocked.Length > 0 ? "Fecha histórica no disponible" : "—"
            : FormatInsightDate(datedUnlocks.Max());

        var total = snapshot.Achievements.Count;
        if (!snapshot.IsCatalogueComplete)
        {
            AchievementProgressText = unlocked.Length == 1
                ? "1 desbloqueado · total desconocido"
                : $"{unlocked.Length} desbloqueados · total desconocido";
        }
        else if (total > 0 && unlocked.Length >= total)
        {
            AchievementProgressText = "100 % completado";
        }
        else if (total > 0)
        {
            AchievementProgressText = $"{unlocked.Length}/{total} · {unlocked.Length * 100d / total:0}%";
        }
        else
        {
            AchievementProgressText = "Sin logros definidos";
        }
    }

    private static string FormatInsightDate(DateTimeOffset value)
    {
        var local = value.ToLocalTime();
        var today = DateTimeOffset.Now.Date;
        if (local.Date == today) return $"Hoy · {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"Ayer · {local:HH:mm}";
        return local.Year == DateTimeOffset.Now.Year
            ? local.ToString("dd MMM · HH:mm")
            : local.ToString("dd/MM/yy · HH:mm");
    }

    private void ConfigureAchievementWatcher(string? statePath)
    {
        StopAchievementWatcher();
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath)) return;

        var directory = Path.GetDirectoryName(statePath);
        var fileName = Path.GetFileName(statePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName)) return;

        try
        {
            _achievementWatcher = new FileSystemWatcher(directory, fileName)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.FileName |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            _achievementWatcher.Changed += AchievementStateFileChanged;
            _achievementWatcher.Created += AchievementStateFileChanged;
            _achievementWatcher.Deleted += AchievementStateFileChanged;
            _achievementWatcher.Renamed += AchievementStateFileChanged;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            StopAchievementWatcher();
        }
    }

    private void AchievementStateFileChanged(object sender, FileSystemEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                _achievementRefreshTimer.Stop();
                _achievementRefreshTimer.Start();
            }));
    }

    private void StopAchievementWatcher()
    {
        if (_achievementWatcher is null) return;
        try
        {
            _achievementWatcher.EnableRaisingEvents = false;
            _achievementWatcher.Changed -= AchievementStateFileChanged;
            _achievementWatcher.Created -= AchievementStateFileChanged;
            _achievementWatcher.Deleted -= AchievementStateFileChanged;
            _achievementWatcher.Renamed -= AchievementStateFileChanged;
            _achievementWatcher.Dispose();
        }
        catch { }
        finally { _achievementWatcher = null; }
    }

    private void SetUnavailable(string detail, string? source = null)
    {
        AchievementCountText = "—";
        AchievementSourceText = source ?? "Sin fuente local compatible";
        AchievementStatusText = detail;
    }

    private void ResetInsights()
    {
        _activityTelemetryText = null;
        HistoricalSourceText = "Sin histórico recuperado";
        HistoricalCoverageText = "No hay una ventana de evidencia histórica guardada.";
        FirstAchievementText = "—";
        LastAchievementText = "—";
        AchievementProgressText = "Sin datos persistidos";
        ActivitySummaryText = "Todavía no hay sesiones o logros persistidos para este juego.";
        FocusedTotalText = "—";
        ActiveTotalText = "—";
        AfkTotalText = "—";
        AttentionCoverageText = "Sin telemetría de atención persistida.";
        RecentActivity.Clear();
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }

    public sealed class AchievementRowViewModel : INotifyPropertyChanged
    {
        private ImageSource? _icon;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ImageSource? Icon
        {
            get => _icon;
            private set
            {
                if (ReferenceEquals(_icon, value)) return;
                _icon = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            }
        }

        public string Title { get; }
        public string Description { get; }
        public string StatusText { get; }
        public string ApiName { get; }
        public double IconOpacity { get; }

        public AchievementRowViewModel(
            LocalAchievement achievement,
            bool partialState = false,
            bool suppressUnlockTime = false,
            bool historicalTimeUnverified = false)
        {
            ApiName = achievement.ApiName;
            var hideDetails = achievement.Hidden && !achievement.IsUnlocked;
            Title = hideDetails ? "Logro oculto" : achievement.DisplayName;
            Description = hideDetails
                ? "La descripción se mostrará cuando se desbloquee."
                : string.IsNullOrWhiteSpace(achievement.Description)
                    ? partialState ? "Metadata del logro no disponible en esta fuente local." : achievement.ApiName
                    : achievement.Description;

            var iconPath = achievement.IsUnlocked
                ? achievement.IconPath
                : achievement.LockedIconPath ?? achievement.IconPath;
            Icon = LocalAchievementImageService.TryLoad(iconPath);
            if (Icon is null && !string.IsNullOrWhiteSpace(iconPath))
            {
                _ = LoadIconAsync(iconPath);
            }
            IconOpacity = achievement.IsUnlocked ? 1d : 0.58d;

            if (achievement.IsUnlocked)
            {
                StatusText = historicalTimeUnverified
                    ? "Desbloqueado · hora histórica no disponible"
                    : suppressUnlockTime || achievement.UnlockedAtUtc is null
                        ? "Desbloqueado"
                        : $"Desbloqueado · {FormatUnlockDate(achievement.UnlockedAtUtc.Value)}";
            }
            else if (achievement.Progress is long progress && achievement.MaxProgress is long maxProgress && maxProgress > 0)
            {
                StatusText = $"Bloqueado · {progress}/{maxProgress}";
            }
            else
            {
                StatusText = "Bloqueado";
            }
        }

        private async Task LoadIconAsync(string imageReference)
        {
            var loaded = await LocalAchievementImageService.LoadAsync(imageReference);
            if (loaded is not null)
            {
                Icon = loaded;
            }
        }

        private static string FormatUnlockDate(DateTimeOffset unlockedAtUtc)
        {
            var local = unlockedAtUtc.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            if (local.Date == today) return $"Hoy · {local:HH:mm}";
            if (local.Date == today.AddDays(-1)) return $"Ayer · {local:HH:mm}";
            return local.Year == DateTimeOffset.Now.Year
                ? local.ToString("dd MMM · HH:mm")
                : local.ToString("dd/MM/yy · HH:mm");
        }
    }
}
