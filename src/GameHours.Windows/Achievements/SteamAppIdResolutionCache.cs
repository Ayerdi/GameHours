using System.Text.Json;

namespace GameHours.Windows.Achievements;

/// <summary>
/// Machine-local cache for already verified Steam AppID identities. The cache is deliberately
/// derived data rather than user data: it is ignored whenever the executable fingerprint changes
/// and current local evidence always wins. A cached identity is only a fallback when its original
/// evidence file has disappeared, never when that file still exists but no longer parses.
/// </summary>
internal sealed class SteamAppIdResolutionCache
{
    private const int FormatVersion = 1;
    private const int MaxEntries = 512;
    private readonly object _gate = new();
    private readonly string _path;

    public SteamAppIdResolutionCache(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = Path.GetFullPath(path);
    }

    public SteamAppIdResolution? TryRead(string executablePath)
    {
        var executable = NormalizePath(executablePath);
        var stamp = TryReadStamp(executable);
        if (stamp is null)
        {
            return null;
        }

        lock (_gate)
        {
            var document = TryLoad();
            var entry = document.Entries
                .FirstOrDefault(item => string.Equals(
                    item.ExecutablePath,
                    executable,
                    StringComparison.OrdinalIgnoreCase));
            if (entry is null ||
                entry.ExecutableLength != stamp.Value.Length ||
                entry.ExecutableLastWriteUtcTicks != stamp.Value.LastWriteUtcTicks ||
                NormalizeAppId(entry.AppId) is not { } appId)
            {
                return null;
            }

            // If the original evidence still exists but the live resolver could not read it,
            // fail closed instead of hiding a malformed or contradictory configuration behind cache.
            if (!string.IsNullOrWhiteSpace(entry.EvidencePath) && File.Exists(entry.EvidencePath))
            {
                return null;
            }

            return new SteamAppIdResolution(
                appId,
                string.IsNullOrWhiteSpace(entry.EvidenceSource)
                    ? "verified local identity cache"
                    : entry.EvidenceSource,
                entry.EvidencePath,
                Enum.IsDefined(typeof(SteamAppIdConfidence), entry.Confidence)
                    ? (SteamAppIdConfidence)entry.Confidence
                    : SteamAppIdConfidence.Medium,
                FromPersistentCache: true);
        }
    }

    public void TryWrite(string executablePath, SteamAppIdResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (resolution.FromPersistentCache || NormalizeAppId(resolution.AppId) is null)
        {
            return;
        }

        var executable = NormalizePath(executablePath);
        var stamp = TryReadStamp(executable);
        if (stamp is null)
        {
            return;
        }

        lock (_gate)
        {
            var document = TryLoad();
            document.Entries.RemoveAll(item => string.Equals(
                item.ExecutablePath,
                executable,
                StringComparison.OrdinalIgnoreCase));
            document.Entries.Add(new CacheEntry(
                executable,
                resolution.AppId,
                resolution.EvidenceSource,
                resolution.EvidencePath,
                (int)resolution.Confidence,
                stamp.Value.Length,
                stamp.Value.LastWriteUtcTicks,
                DateTimeOffset.UtcNow));

            if (document.Entries.Count > MaxEntries)
            {
                document.Entries = document.Entries
                    .OrderByDescending(item => item.VerifiedAtUtc)
                    .Take(MaxEntries)
                    .ToList();
            }

            TrySave(document);
        }
    }

    private CacheDocument TryLoad()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return NewDocument();
            }

            var parsed = JsonSerializer.Deserialize<CacheDocument>(File.ReadAllText(_path));
            return parsed is not null && parsed.Version == FormatVersion && parsed.Entries is not null
                ? parsed
                : NewDocument();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            JsonException or NotSupportedException or PathTooLongException)
        {
            return NewDocument();
        }
    }

    private void TrySave(CacheDocument document)
    {
        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(_path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(document));
            File.Move(temporaryPath, _path, overwrite: true);
            temporaryPath = null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            // This is derived cache only. Failure must never block achievement detection.
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(temporaryPath))
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private static CacheDocument NewDocument() => new()
    {
        Version = FormatVersion,
        Entries = new List<CacheEntry>()
    };

    private static string NormalizePath(string path) => Path.GetFullPath(path);

    private static ExecutableStamp? TryReadStamp(string executablePath)
    {
        try
        {
            var file = new FileInfo(executablePath);
            return file.Exists
                ? new ExecutableStamp(file.Length, file.LastWriteTimeUtc.Ticks)
                : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? NormalizeAppId(string? value)
    {
        var normalized = value?.Trim();
        return !string.IsNullOrWhiteSpace(normalized) && normalized.All(char.IsDigit)
            ? normalized
            : null;
    }

    private readonly record struct ExecutableStamp(long Length, long LastWriteUtcTicks);

    private sealed class CacheDocument
    {
        public int Version { get; set; }
        public List<CacheEntry> Entries { get; set; } = new();
    }

    private sealed record CacheEntry(
        string ExecutablePath,
        string AppId,
        string EvidenceSource,
        string? EvidencePath,
        int Confidence,
        long ExecutableLength,
        long ExecutableLastWriteUtcTicks,
        DateTimeOffset VerifiedAtUtc);
}
