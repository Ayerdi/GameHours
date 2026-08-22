using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameHours.Desktop;

public sealed record DesktopPreferences(int AfkTimeoutMinutes, bool LowImpactMode)
{
    public const int DefaultAfkTimeoutMinutes = 5;

    public static DesktopPreferences Default { get; } = new(DefaultAfkTimeoutMinutes, true);

    public bool AfkFilterEnabled => AfkTimeoutMinutes > 0;

    public TimeSpan IdleThreshold => AfkFilterEnabled
        ? TimeSpan.FromMinutes(AfkTimeoutMinutes)
        : TimeSpan.Zero;

    public static bool IsSupportedAfkTimeout(int minutes) =>
        minutes is 0 or 2 or 5 or 10 or 15;

    public DesktopPreferences Normalize() =>
        this with
        {
            AfkTimeoutMinutes = IsSupportedAfkTimeout(AfkTimeoutMinutes)
                ? AfkTimeoutMinutes
                : DefaultAfkTimeoutMinutes
        };
}

public sealed class DesktopPreferencesStore
{
    private const int CurrentVersion = 1;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly string _path;
    private DesktopPreferences? _cached;

    public DesktopPreferencesStore(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHours",
            "settings.json");
    }

    public string Path => _path;

    public DesktopPreferences Current
    {
        get
        {
            lock (_gate)
            {
                return _cached ??= LoadCore();
            }
        }
    }

    public event Action<DesktopPreferences>? Changed;

    public DesktopPreferences Reload()
    {
        DesktopPreferences value;
        lock (_gate)
        {
            value = LoadCore();
            _cached = value;
        }

        return value;
    }

    public void Save(DesktopPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        var document = new PreferencesDocument(
            CurrentVersion,
            normalized.AfkTimeoutMinutes,
            normalized.LowImpactMode);
        var json = JsonSerializer.Serialize(document, JsonOptions);

        var directory = System.IO.Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("Preferences path must have a parent directory.");
        }

        Directory.CreateDirectory(directory);
        var temporaryPath = System.IO.Path.Combine(
            directory,
            $".{System.IO.Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(temporaryPath, json);
            if (File.Exists(_path))
            {
                File.Replace(temporaryPath, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, _path);
            }
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
                // A stale temporary preference file is harmless and must not mask the successful
                // write (or the original write failure).
            }
        }

        var changed = false;
        lock (_gate)
        {
            changed = _cached != normalized;
            _cached = normalized;
        }

        if (changed)
        {
            Changed?.Invoke(normalized);
        }
    }

    private DesktopPreferences LoadCore()
    {
        try
        {
            if (!File.Exists(_path)) return DesktopPreferences.Default;
            var json = File.ReadAllText(_path);
            var document = JsonSerializer.Deserialize<PreferencesDocument>(json, JsonOptions);
            if (document is null || document.Version != CurrentVersion)
            {
                return DesktopPreferences.Default;
            }

            return new DesktopPreferences(
                    document.AfkTimeoutMinutes,
                    document.LowImpactMode)
                .Normalize();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return DesktopPreferences.Default;
        }
    }

    private sealed record PreferencesDocument(
        int Version,
        int AfkTimeoutMinutes,
        bool LowImpactMode);
}
