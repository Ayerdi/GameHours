using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using GameHours.Windows.Achievements;

namespace GameHours.Desktop;

public partial class GameDetailView : System.Windows.Controls.UserControl, INotifyPropertyChanged
{
    private readonly ILocalAchievementProvider _achievementProvider = new AggregatingLocalAchievementProvider();
    private readonly DispatcherTimer _achievementRefreshTimer;
    private FileSystemWatcher? _achievementWatcher;
    private string? _currentExecutablePath;
    private string _achievementCountText = "—";
    private string _achievementSourceText = "Sin fuente local compatible";
    private string _achievementStatusText = "GameHours todavía no ha detectado logros locales para este juego.";

    public event EventHandler? BackRequested;
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AchievementRowViewModel> AchievementRows { get; } = new();

    public string AchievementCountText
    {
        get => _achievementCountText;
        private set => SetField(ref _achievementCountText, value);
    }

    public string AchievementSourceText
    {
        get => _achievementSourceText;
        private set => SetField(ref _achievementSourceText, value);
    }

    public string AchievementStatusText
    {
        get => _achievementStatusText;
        private set => SetField(ref _achievementStatusText, value);
    }

    public GameDetailView()
    {
        InitializeComponent();
        DataContextChanged += GameDetailView_DataContextChanged;

        _achievementRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(450)
        };
        _achievementRefreshTimer.Tick += (_, _) =>
        {
            _achievementRefreshTimer.Stop();
            LoadAchievements(_currentExecutablePath);
        };
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        BackRequested?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshAchievements_Click(object sender, RoutedEventArgs e)
    {
        LoadAchievements(_currentExecutablePath);
    }

    private void GameDetailView_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        _currentExecutablePath = e.NewValue is MainWindow.GameDetailViewModel detail &&
                                 !string.Equals(detail.ExecutableText, "Sin ejecutable asociado", StringComparison.Ordinal)
            ? detail.ExecutableText
            : null;

        LoadAchievements(_currentExecutablePath);
    }

    private void LoadAchievements(string? executablePath)
    {
        AchievementRows.Clear();

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
            SetUnavailable("No se ha detectado ninguna fuente local de logros compatible para este juego.");
            return;
        }

        ConfigureAchievementWatcher(snapshot.StatePath);

        var total = snapshot.Achievements.Count;
        var unlocked = snapshot.UnlockedCount;
        var partialState = !snapshot.IsCatalogueComplete;

        AchievementCountText = partialState
            ? $"{unlocked} desbloq."
            : $"{unlocked}/{total}";
        AchievementSourceText = string.IsNullOrWhiteSpace(snapshot.AppId)
            ? snapshot.Source
            : $"{snapshot.Source} · AppID {snapshot.AppId}";

        if (partialState)
        {
            AchievementStatusText = unlocked == 1
                ? "1 logro desbloqueado detectado localmente · el catálogo completo no está disponible en esta fuente."
                : $"{unlocked} logros desbloqueados detectados localmente · el catálogo completo no está disponible en esta fuente.";
        }
        else if (snapshot.StatePath is null)
        {
            AchievementStatusText = "Se encontraron las definiciones, pero todavía no existe un estado local de logros del usuario.";
        }
        else
        {
            var percentage = total == 0 ? 0d : unlocked * 100d / total;
            AchievementStatusText = $"{percentage:0}% completado · estado leído localmente, sin Internet.";
        }

        foreach (var achievement in snapshot.Achievements
                     .OrderByDescending(item => item.IsUnlocked)
                     .ThenBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase))
        {
            AchievementRows.Add(new AchievementRowViewModel(achievement, partialState));
        }
    }

    private void ConfigureAchievementWatcher(string? statePath)
    {
        StopAchievementWatcher();
        if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
        {
            return;
        }

        var directory = Path.GetDirectoryName(statePath);
        var fileName = Path.GetFileName(statePath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

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
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

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
        if (_achievementWatcher is null)
        {
            return;
        }

        try
        {
            _achievementWatcher.EnableRaisingEvents = false;
            _achievementWatcher.Changed -= AchievementStateFileChanged;
            _achievementWatcher.Created -= AchievementStateFileChanged;
            _achievementWatcher.Deleted -= AchievementStateFileChanged;
            _achievementWatcher.Renamed -= AchievementStateFileChanged;
            _achievementWatcher.Dispose();
        }
        catch
        {
        }
        finally
        {
            _achievementWatcher = null;
        }
    }

    private void SetUnavailable(string detail)
    {
        AchievementCountText = "—";
        AchievementSourceText = "Sin fuente local compatible";
        AchievementStatusText = detail;
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

    public sealed class AchievementRowViewModel
    {
        public ImageSource? Icon { get; }
        public string Title { get; }
        public string Description { get; }
        public string StatusText { get; }
        public string ApiName { get; }
        public double IconOpacity { get; }

        public AchievementRowViewModel(LocalAchievement achievement, bool partialState = false)
        {
            ApiName = achievement.ApiName;
            var hideDetails = achievement.Hidden && !achievement.IsUnlocked;
            Title = hideDetails ? "Logro oculto" : achievement.DisplayName;
            Description = hideDetails
                ? "La descripción se mostrará cuando se desbloquee."
                : string.IsNullOrWhiteSpace(achievement.Description)
                    ? partialState
                        ? "Metadata del logro no disponible en esta fuente local."
                        : achievement.ApiName
                    : achievement.Description;

            var iconPath = achievement.IsUnlocked
                ? achievement.IconPath
                : achievement.LockedIconPath ?? achievement.IconPath;
            Icon = LocalAchievementImageService.TryLoad(iconPath);
            IconOpacity = achievement.IsUnlocked ? 1d : 0.58d;

            if (achievement.IsUnlocked)
            {
                StatusText = achievement.UnlockedAtUtc is null
                    ? "Desbloqueado"
                    : $"Desbloqueado · {FormatUnlockDate(achievement.UnlockedAtUtc.Value)}";
            }
            else if (achievement.Progress is long progress &&
                     achievement.MaxProgress is long maxProgress &&
                     maxProgress > 0)
            {
                StatusText = $"Bloqueado · {progress}/{maxProgress}";
            }
            else
            {
                StatusText = "Bloqueado";
            }
        }

        private static string FormatUnlockDate(DateTimeOffset unlockedAtUtc)
        {
            var local = unlockedAtUtc.ToLocalTime();
            var today = DateTimeOffset.Now.Date;
            if (local.Date == today)
            {
                return $"Hoy · {local:HH:mm}";
            }

            if (local.Date == today.AddDays(-1))
            {
                return $"Ayer · {local:HH:mm}";
            }

            return local.Year == DateTimeOffset.Now.Year
                ? local.ToString("dd MMM · HH:mm")
                : local.ToString("dd/MM/yy · HH:mm");
        }
    }
}
