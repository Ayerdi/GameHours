using GameHours.Core.Domain;

namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// Minimal, format-neutral projection of Gothic 1 Remake save data. It deliberately makes no
/// assertion about which entries correspond to achievements; that mapping belongs to injected
/// positive-only rules once the save format is understood.
/// </summary>
public sealed record Gothic1RemakeSaveState(
    IReadOnlySet<string> CompletedQuests,
    IReadOnlySet<string> InventoryItems,
    IReadOnlySet<string> LearnedSkills,
    IReadOnlySet<string> GlossaryEntries);

/// <summary>
/// Reads a Gothic 1 Remake save into the small state projection used by evidence rules. The
/// first implementation intentionally does not prescribe a GVAS or Oodle decoder.
/// </summary>
public interface IGothic1RemakeSaveParser
{
    Task<Gothic1RemakeSaveState> ParseAsync(string savePath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves the game's conventional save folder. A path can be supplied for deterministic tests
/// or an alternate local installation without touching the provider's parsing behavior.
/// </summary>
public sealed class Gothic1RemakeSaveDirectoryLocator
{
    private readonly string? _saveDirectory;

    public Gothic1RemakeSaveDirectoryLocator(string? saveDirectory = null)
    {
        _saveDirectory = string.IsNullOrWhiteSpace(saveDirectory) ? null : saveDirectory.Trim();
    }

    public string GetSaveDirectory() => _saveDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G1R",
        "Saved",
        "SaveGames");
}

/// <summary>
/// Read-only evidence provider for the Steam release of Gothic 1 Remake (app 1297900). Save
/// parsing is cached only while a file's inexpensive length/timestamp metadata remains unchanged.
/// </summary>
public sealed class Gothic1RemakeSaveEvidenceProvider : IAchievementUnlockEvidenceProvider
{
    public const string SteamAppId = "1297900";
    public const string ProviderName = "gothic-1-remake-save";

    private readonly IGothic1RemakeSaveParser _parser;
    private readonly IReadOnlyList<IAchievementEvidenceRule<Gothic1RemakeSaveState>> _rules;
    private readonly Gothic1RemakeSaveDirectoryLocator _saveDirectoryLocator;
    private readonly Dictionary<string, CachedSave> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _cacheGate = new(1, 1);

    public Gothic1RemakeSaveEvidenceProvider(
        IGothic1RemakeSaveParser parser,
        IEnumerable<IAchievementEvidenceRule<Gothic1RemakeSaveState>> rules,
        Gothic1RemakeSaveDirectoryLocator? saveDirectoryLocator = null)
    {
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(rules);
        _parser = parser;
        _rules = rules.ToArray();
        _saveDirectoryLocator = saveDirectoryLocator ?? new Gothic1RemakeSaveDirectoryLocator();
    }

    public string Name => ProviderName;

    public async Task<AchievementEvidenceReadResult> ReadAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        request = request.Normalize();
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.PlatformAppId, SteamAppId, StringComparison.Ordinal))
        {
            return AchievementEvidenceReadResult.NotApplicable(Name);
        }

        var saveDirectory = _saveDirectoryLocator.GetSaveDirectory();
        if (!Directory.Exists(saveDirectory))
        {
            return AchievementEvidenceReadResult.NoEvidence(Name);
        }

        string[] savePaths;
        try
        {
            savePaths = Directory.EnumerateFiles(saveDirectory, "*.sav", SearchOption.TopDirectoryOnly)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception) when (IsFileFailure(exception))
        {
            return AchievementEvidenceReadResult.Failure(Name, exception.Message, saveDirectory);
        }

        var evidence = new List<ConfirmedAchievementUnlockEvidence>();
        var diagnostics = new List<AchievementEvidenceDiagnostic>();

        foreach (var savePath in savePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var read = await ReadStateAsync(savePath, cancellationToken);
                evidence.AddRange(AchievementEvidenceRuleEvaluator.Evaluate(
                    request.GameId,
                    AchievementEvidenceOrigin.SaveGame,
                    Name,
                    read.State,
                    _rules,
                    savePath,
                    read.Metadata.Fingerprint,
                    request.ObservedAtUtc));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsFileFailure(exception))
            {
                diagnostics.Add(new AchievementEvidenceDiagnostic(Name, exception.Message, savePath));
            }
        }

        var uniqueEvidence = evidence
            .GroupBy(item => string.Join('\u001f', item.ApiName, item.RuleId, item.RuleVersion, item.SourcePath, item.SourceFingerprint), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToArray();

        if (uniqueEvidence.Length > 0)
        {
            return new AchievementEvidenceReadResult(Name, AchievementEvidenceReadStatus.Success, uniqueEvidence, diagnostics);
        }

        if (diagnostics.Count > 0)
        {
            return new AchievementEvidenceReadResult(Name, AchievementEvidenceReadStatus.Failed, uniqueEvidence, diagnostics);
        }

        return AchievementEvidenceReadResult.NoEvidence(Name);
    }

    private async Task<ReadSaveResult> ReadStateAsync(
        string savePath,
        CancellationToken cancellationToken)
    {
        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            var metadata = ReadMetadata(savePath);
            if (_cache.TryGetValue(savePath, out var cached) && cached.Metadata == metadata)
            {
                return new ReadSaveResult(cached.State, metadata);
            }

            var state = await _parser.ParseAsync(savePath, cancellationToken);
            ArgumentNullException.ThrowIfNull(state);

            var metadataAfterRead = ReadMetadata(savePath);
            if (metadataAfterRead != metadata)
            {
                throw new IOException("Save changed while it was being inspected; evidence was not emitted.");
            }

            _cache[savePath] = new CachedSave(metadata, state);
            return new ReadSaveResult(state, metadata);
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    private static SaveMetadata ReadMetadata(string savePath)
    {
        var file = new FileInfo(savePath);
        return new SaveMetadata(file.Length, file.LastWriteTimeUtc);
    }

    private static bool IsFileFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InvalidDataException or FormatException;

    private sealed record CachedSave(SaveMetadata Metadata, Gothic1RemakeSaveState State);

    private sealed record ReadSaveResult(Gothic1RemakeSaveState State, SaveMetadata Metadata);

    private sealed record SaveMetadata(long Length, DateTime LastWriteTimeUtc)
    {
        public string Fingerprint => $"meta:v1:{Length}:{LastWriteTimeUtc.Ticks}";
    }
}
