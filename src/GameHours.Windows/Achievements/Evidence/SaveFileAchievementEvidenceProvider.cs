using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>Parses one save file into the state projection understood by evidence rules.</summary>
public interface ISaveStateParser<TState>
{
    /// <remarks>
    /// Unsupported or malformed formats should be reported as <see cref="InvalidDataException"/>
    /// so one bad save can be isolated without hiding programming errors. Implementations must
    /// support concurrent calls for different files.
    /// </remarks>
    Task<TState> ParseAsync(string savePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Reusable, read-only provider for achievements that can be positively proved from save files.
/// A profile supplies applicability, discovery and parsing as delegates; this type owns caching,
/// metadata fingerprints, per-file parse sharing and diagnostics.
/// </summary>
public sealed class SaveFileAchievementEvidenceProvider<TState> : IAchievementUnlockEvidenceProvider, IDisposable
{
    private readonly Func<AchievementEvidenceRequest, bool> _isApplicable;
    private readonly Func<IEnumerable<string>> _locateSaveFiles;
    private readonly ISaveStateParser<TState> _parser;
    private readonly IReadOnlyList<IAchievementEvidenceRule<TState>> _rules;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _parseSlots;
    private readonly CancellationTokenSource _lifetime = new();
    private int _disposed;

    public SaveFileAchievementEvidenceProvider(
        string name,
        Func<AchievementEvidenceRequest, bool> isApplicable,
        Func<IEnumerable<string>> locateSaveFiles,
        ISaveStateParser<TState> parser,
        IEnumerable<IAchievementEvidenceRule<TState>> rules,
        int maxConcurrentParses = 2)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Evidence provider cannot be empty.", nameof(name));
        }

        ArgumentNullException.ThrowIfNull(isApplicable);
        ArgumentNullException.ThrowIfNull(locateSaveFiles);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(rules);
        if (maxConcurrentParses <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxConcurrentParses),
                "Concurrent parse limit must be positive.");
        }

        Name = name.Trim();
        _isApplicable = isApplicable;
        _locateSaveFiles = locateSaveFiles;
        _parser = parser;
        _rules = rules.ToArray();
        _parseSlots = new SemaphoreSlim(maxConcurrentParses, maxConcurrentParses);
    }

    public string Name { get; }

    public async Task<AchievementEvidenceReadResult> ReadAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(request);
        request = request.Normalize();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_isApplicable(request))
        {
            return AchievementEvidenceReadResult.NotApplicable(Name);
        }

        string[] savePaths;
        try
        {
            var locatedFiles = _locateSaveFiles()
                ?? throw new InvalidOperationException("Save file locator returned null.");
            var candidates = locatedFiles.ToArray();
            if (candidates.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("Save file locator returned an empty path.");
            }

            savePaths = candidates
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (DirectoryNotFoundException)
        {
            return AchievementEvidenceReadResult.NoEvidence(Name);
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return AchievementEvidenceReadResult.Failure(Name, exception.Message);
        }

        PruneCache(savePaths);

        var reads = await Task.WhenAll(savePaths.Select(savePath => ReadFileAsync(savePath, request, cancellationToken)));
        var evidence = reads.SelectMany(read => read.Evidence).ToArray();
        var diagnostics = reads.Where(read => read.Diagnostic is not null).Select(read => read.Diagnostic!).ToArray();

        var uniqueEvidence = evidence
            .GroupBy(
                item => string.Join(
                    '\u001f',
                    item.ApiName,
                    item.RuleId,
                    item.RuleVersion,
                    NormalizePathForKey(item.SourcePath),
                    item.SourceFingerprint),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        return uniqueEvidence.Length > 0
            ? new AchievementEvidenceReadResult(Name, AchievementEvidenceReadStatus.Success, uniqueEvidence, diagnostics)
            : diagnostics.Length > 0
                ? new AchievementEvidenceReadResult(Name, AchievementEvidenceReadStatus.Failed, uniqueEvidence, diagnostics)
                : AchievementEvidenceReadResult.NoEvidence(Name);
    }

    private async Task<FileReadOutcome> ReadFileAsync(
        string savePath,
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadSaveResult read;
        try
        {
            read = await ReadStateAsync(savePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            // A malformed or unsupported save must not prevent evidence from sibling saves.
            return new FileReadOutcome(Array.Empty<ConfirmedAchievementUnlockEvidence>(), new AchievementEvidenceDiagnostic(Name, exception.Message, savePath));
        }

        return new FileReadOutcome(AchievementEvidenceRuleEvaluator.Evaluate(
            request.GameId,
            AchievementEvidenceOrigin.SaveGame,
            Name,
            read.State,
            _rules,
            savePath,
            read.Metadata.Fingerprint,
            request.ObservedAtUtc), null);
    }

    private async Task<ReadSaveResult> ReadStateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        var cacheKey = NormalizePathForKey(sourcePath);
        while (true)
        {
            var metadata = ReadMetadata(sourcePath);
            if (_cache.TryGetValue(cacheKey, out var current) && current.Metadata == metadata)
            {
                ReadSaveResult cachedRead;
                try
                {
                    cachedRead = await AwaitSharedReadAsync(cacheKey, current, cancellationToken);
                }
                catch (SourceChangedDuringReadException)
                {
                    continue;
                }

                if (ReadMetadata(sourcePath) == cachedRead.Metadata)
                {
                    return cachedRead;
                }

                RemoveCacheEntry(cacheKey, current);
                continue;
            }

            if (current is not null)
            {
                try
                {
                    _ = await AwaitSharedReadAsync(cacheKey, current, cancellationToken);
                }
                catch (Exception exception) when (IsFileFailure(exception))
                {
                    // The previous file version was unreadable or changed mid-read. This caller
                    // observed different metadata, so it may safely retry the newer version.
                }

                RemoveCacheEntry(cacheKey, current);
                continue;
            }

            var candidate = new CacheEntry(
                metadata,
                new Lazy<Task<ReadSaveResult>>(
                    () => ParseStableAsync(sourcePath, metadata),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            if (_cache.TryAdd(cacheKey, candidate) ||
                (current is not null && _cache.TryUpdate(cacheKey, candidate, current)))
            {
                return await AwaitSharedReadAsync(cacheKey, candidate, cancellationToken);
            }
        }
    }

    private async Task<ReadSaveResult> AwaitSharedReadAsync(
        string cacheKey,
        CacheEntry entry,
        CancellationToken cancellationToken)
    {
        try
        {
            return await entry.Read.Value.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            RemoveCacheEntry(cacheKey, entry);
            throw;
        }
    }

    private void RemoveCacheEntry(string cacheKey, CacheEntry entry) =>
        ((ICollection<KeyValuePair<string, CacheEntry>>)_cache).Remove(
            new KeyValuePair<string, CacheEntry>(cacheKey, entry));

    private async Task<ReadSaveResult> ParseStableAsync(
        string sourcePath,
        SaveMetadata metadata)
    {
        await _parseSlots.WaitAsync(_lifetime.Token);
        try
        {
            var state = await _parser.ParseAsync(sourcePath, _lifetime.Token);
            ArgumentNullException.ThrowIfNull(state);
            if (ReadMetadata(sourcePath) != metadata)
            {
                throw new SourceChangedDuringReadException();
            }

            return new ReadSaveResult(state, metadata);
        }
        finally
        {
            _parseSlots.Release();
        }
    }

    private void PruneCache(IReadOnlyCollection<string> activePaths)
    {
        var activeKeys = activePaths
            .Select(NormalizePathForKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _cache.Keys)
        {
            if (!activeKeys.Contains(key) &&
                _cache.TryGetValue(key, out var entry) &&
                entry.Read.IsValueCreated &&
                entry.Read.Value.IsCompleted)
            {
                RemoveCacheEntry(key, entry);
            }
        }
    }

    private static SaveMetadata ReadMetadata(string savePath)
    {
        var file = new FileInfo(savePath);
        var normalizedPath = NormalizePathForKey(savePath).ToUpperInvariant();
        var pathHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalizedPath)));
        return new SaveMetadata(pathHash, file.Length, file.LastWriteTimeUtc);
    }

    private static string NormalizePathForKey(string? path) => string.IsNullOrWhiteSpace(path)
        ? string.Empty
        : Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _lifetime.Cancel();
        }
    }

    private sealed record CacheEntry(SaveMetadata Metadata, Lazy<Task<ReadSaveResult>> Read);
    private sealed record FileReadOutcome(IReadOnlyList<ConfirmedAchievementUnlockEvidence> Evidence, AchievementEvidenceDiagnostic? Diagnostic);
    private sealed record ReadSaveResult(TState State, SaveMetadata Metadata);
    private sealed record SaveMetadata(string PathHash, long Length, DateTime LastWriteTimeUtc)
    {
        public string Fingerprint => $"meta:v2:{PathHash}:{Length}:{LastWriteTimeUtc.Ticks}";
    }

    private sealed class SourceChangedDuringReadException()
        : IOException("Save changed while it was being inspected; evidence was not emitted.");
}
