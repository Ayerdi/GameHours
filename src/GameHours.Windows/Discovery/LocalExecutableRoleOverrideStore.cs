using System.Text.Json;
using GameHours.Core.Discovery;

namespace GameHours.Windows.Discovery;

public interface IExecutableRoleOverrideStore
{
    bool TryGetRole(string executablePath, out ExecutableRole role);
    void SetRole(string executablePath, ExecutableRole role);
    void Remove(string executablePath);
}

public sealed class LocalExecutableRoleOverrideStore : IExecutableRoleOverrideStore
{
    private readonly string _filePath;
    private readonly object _gate = new();
    private Dictionary<string, ExecutableRole> _items = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _loadedWriteTimeUtc = DateTime.MinValue;
    private bool _loaded;

    public LocalExecutableRoleOverrideStore(string? filePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(filePath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "GameHours",
                "executable-role-overrides.json")
            : Path.GetFullPath(filePath);
    }

    public bool TryGetRole(string executablePath, out ExecutableRole role)
    {
        var normalized = NormalizePath(executablePath);
        if (normalized is null)
        {
            role = ExecutableRole.Unknown;
            return false;
        }

        lock (_gate)
        {
            ReloadIfChanged();
            return _items.TryGetValue(normalized, out role);
        }
    }

    public void SetRole(string executablePath, ExecutableRole role)
    {
        var normalized = NormalizePath(executablePath)
            ?? throw new ArgumentException("Executable path is invalid.", nameof(executablePath));

        lock (_gate)
        {
            ReloadIfChanged();
            _items[normalized] = role;
            Save();
        }
    }

    public void Remove(string executablePath)
    {
        var normalized = NormalizePath(executablePath);
        if (normalized is null)
        {
            return;
        }

        lock (_gate)
        {
            ReloadIfChanged();
            if (_items.Remove(normalized))
            {
                Save();
            }
        }
    }

    private void ReloadIfChanged()
    {
        DateTime writeTimeUtc;
        try
        {
            writeTimeUtc = File.Exists(_filePath)
                ? File.GetLastWriteTimeUtc(_filePath)
                : DateTime.MinValue;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return;
        }

        if (_loaded && writeTimeUtc == _loadedWriteTimeUtc)
        {
            return;
        }

        _loaded = true;
        _loadedWriteTimeUtc = writeTimeUtc;
        _items = new Dictionary<string, ExecutableRole>(StringComparer.OrdinalIgnoreCase);
        if (writeTimeUtc == DateTime.MinValue)
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_filePath);
            var serialized = JsonSerializer.Deserialize<Dictionary<string, ExecutableRole>>(json);
            if (serialized is null)
            {
                return;
            }

            foreach (var pair in serialized)
            {
                var normalized = NormalizePath(pair.Key);
                if (normalized is not null)
                {
                    _items[normalized] = pair.Value;
                }
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            _items.Clear();
        }
    }

    private void Save()
    {
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = _filePath + ".tmp";
        var json = JsonSerializer.Serialize(
            _items,
            new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, _filePath, overwrite: true);
        _loadedWriteTimeUtc = File.GetLastWriteTimeUtc(_filePath);
        _loaded = true;
    }

    private static string? NormalizePath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(executablePath.Trim().Trim('"'));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
