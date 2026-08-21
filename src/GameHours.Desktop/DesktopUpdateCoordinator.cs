using System.Reflection;
using System.Text.Json;
using GameHours.Core.Abstractions;
using GameHours.Core.Updates;
using GameHours.Update;

namespace GameHours.Desktop;

public sealed class DesktopUpdateCoordinator
{
    private sealed record PersistedUpdateState(
        string? LatestNotesVersion,
        string? LatestNotesMarkdown,
        string? LastSeenWhatsNewVersion);

    private sealed class UpdateStateStore
    {
        private readonly string _path;

        public UpdateStateStore()
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHours");
            _path = Path.Combine(directory, "update-state.json");
        }

        public PersistedUpdateState Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return new PersistedUpdateState(null, null, null);
                }

                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<PersistedUpdateState>(json)
                       ?? new PersistedUpdateState(null, null, null);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or JsonException or
                ArgumentException or PathTooLongException or NotSupportedException)
            {
                return new PersistedUpdateState(null, null, null);
            }
        }

        public void Save(PersistedUpdateState state)
        {
            try
            {
                var directory = Path.GetDirectoryName(_path);
                if (string.IsNullOrWhiteSpace(directory))
                {
                    return;
                }

                Directory.CreateDirectory(directory);
                var json = JsonSerializer.Serialize(
                    state,
                    new JsonSerializerOptions { WriteIndented = true });
                var temporaryPath = _path + ".tmp";
                File.WriteAllText(temporaryPath, json);
                File.Move(temporaryPath, _path, overwrite: true);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or ArgumentException or
                PathTooLongException or NotSupportedException)
            {
                // Update-note persistence is optional and must never prevent the app from starting.
            }
        }
    }

    private readonly IAppUpdateService? _service;
    private readonly UpdateStateStore _stateStore = new();
    private readonly string? _bundledNotesMarkdown;
    private PersistedUpdateState _state;

    private DesktopUpdateCoordinator(IAppUpdateService? service)
    {
        _service = service;
        _state = _stateStore.Load();
        _bundledNotesMarkdown = ReadBundledReleaseNotes();
    }

    public static DesktopUpdateCoordinator CreateDefault()
    {
        var source = ResolveUpdateSource();
        if (string.IsNullOrWhiteSpace(source))
        {
            return new DesktopUpdateCoordinator(null);
        }

        try
        {
            return new DesktopUpdateCoordinator(new VelopackUpdateService(source));
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException or UriFormatException)
        {
            return new DesktopUpdateCoordinator(null);
        }
    }

    public bool IsSourceConfigured => _service is not null;

    public bool IsInstalled => _service?.IsInstalled == true;

    public bool CanSelfUpdate => IsSourceConfigured && IsInstalled;

    public string CurrentVersion =>
        _service?.CurrentVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3)
        ?? "desarrollo";

    public string Channel => _service?.Channel ?? "development";

    public string AvailabilityText => !IsSourceConfigured
        ? "El feed de actualizaciones no está configurado en esta compilación."
        : !IsInstalled
            ? "Las compilaciones ejecutadas con dotnet run no se actualizan automáticamente."
            : "GameHours puede buscar e instalar actualizaciones desde la aplicación.";

    public bool HasUnseenWhatsNew =>
        !string.IsNullOrWhiteSpace(InstalledNotesMarkdown) &&
        !VersionsEqual(_state.LastSeenWhatsNewVersion, CurrentVersion);

    public string? InstalledNotesVersion =>
        !string.IsNullOrWhiteSpace(InstalledNotesMarkdown)
            ? CurrentVersion
            : null;

    public string? InstalledNotesMarkdown
    {
        get
        {
            if (VersionsEqual(_state.LatestNotesVersion, CurrentVersion) &&
                !string.IsNullOrWhiteSpace(_state.LatestNotesMarkdown))
            {
                return _state.LatestNotesMarkdown;
            }

            return string.IsNullOrWhiteSpace(_bundledNotesMarkdown)
                ? null
                : _bundledNotesMarkdown;
        }
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!CanSelfUpdate || _service is null)
        {
            return null;
        }

        return await _service.CheckAsync(cancellationToken);
    }

    public async Task DownloadAsync(
        AppUpdate update,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (_service is null || !CanSelfUpdate)
        {
            throw new InvalidOperationException("Self-update is not available in this build.");
        }

        await _service.DownloadAsync(update, progress, cancellationToken);
    }

    public void RememberReleaseNotes(AppUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        _state = _state with
        {
            LatestNotesVersion = update.Version,
            LatestNotesMarkdown = update.ReleaseNotesMarkdown
        };
        _stateStore.Save(_state);
    }

    public void PrepareApplyAndRestart(AppUpdate update, string[]? restartArgs = null)
    {
        if (_service is null || !CanSelfUpdate)
        {
            throw new InvalidOperationException("Self-update is not available in this build.");
        }

        _service.PrepareApplyAndRestart(update, restartArgs);
    }

    public void MarkCurrentWhatsNewSeen()
    {
        if (string.IsNullOrWhiteSpace(InstalledNotesMarkdown))
        {
            return;
        }

        _state = _state with { LastSeenWhatsNewVersion = CurrentVersion };
        _stateStore.Save(_state);
    }

    private static string? ResolveUpdateSource()
    {
        var environmentSource = Environment.GetEnvironmentVariable("GAMEHOURS_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(environmentSource))
        {
            return environmentSource.Trim();
        }

        try
        {
            var sourceFile = Path.Combine(AppContext.BaseDirectory, "update-source.txt");
            if (!File.Exists(sourceFile))
            {
                return null;
            }

            var source = File.ReadAllText(sourceFile).Trim();
            return string.IsNullOrWhiteSpace(source) ? null : source;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadBundledReleaseNotes()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "release-notes.md");
            if (!File.Exists(path))
            {
                return null;
            }

            var notes = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(notes) ? null : notes;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            PathTooLongException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool VersionsEqual(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
