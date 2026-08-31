namespace GameHours.Windows.Achievements.Evidence;

/// <summary>
/// Minimal, format-neutral projection of Gothic 1 Remake save data. Achievement mappings belong
/// to injected positive-only rules once the format is understood.
/// </summary>
public sealed record Gothic1RemakeSaveState(
    IReadOnlySet<string> CompletedQuests,
    IReadOnlySet<string> InventoryItems,
    IReadOnlySet<string> LearnedSkills,
    IReadOnlySet<string> GlossaryEntries);

/// <summary>Resolves Gothic 1 Remake's conventional save folder.</summary>
public sealed class Gothic1RemakeSaveDirectoryLocator
{
    private readonly string? _saveDirectory;

    public Gothic1RemakeSaveDirectoryLocator(string? saveDirectory = null) =>
        _saveDirectory = string.IsNullOrWhiteSpace(saveDirectory) ? null : saveDirectory.Trim();

    public string GetSaveDirectory() => _saveDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "G1R", "Saved", "SaveGames");
}

/// <summary>Thin Gothic profile over the reusable save-evidence infrastructure.</summary>
public sealed class Gothic1RemakeSaveEvidenceProvider : IAchievementUnlockEvidenceProvider, IDisposable
{
    public const string SteamAppId = "1297900";
    public const string ProviderName = "gothic-1-remake-save";

    private readonly SaveFileAchievementEvidenceProvider<Gothic1RemakeSaveState> _inner;

    public Gothic1RemakeSaveEvidenceProvider(
        ISaveStateParser<Gothic1RemakeSaveState> parser,
        IEnumerable<IAchievementEvidenceRule<Gothic1RemakeSaveState>> rules,
        Gothic1RemakeSaveDirectoryLocator? saveDirectoryLocator = null)
    {
        var locator = saveDirectoryLocator ?? new Gothic1RemakeSaveDirectoryLocator();
        _inner = new SaveFileAchievementEvidenceProvider<Gothic1RemakeSaveState>(
            ProviderName,
            request => string.Equals(request.PlatformAppId, SteamAppId, StringComparison.Ordinal),
            () => Directory.EnumerateFiles(
                locator.GetSaveDirectory(),
                "*.sav",
                SearchOption.TopDirectoryOnly),
            parser,
            rules);
    }

    public string Name => _inner.Name;

    public Task<AchievementEvidenceReadResult> ReadAsync(
        AchievementEvidenceRequest request,
        CancellationToken cancellationToken = default) => _inner.ReadAsync(request, cancellationToken);

    public void Dispose() => _inner.Dispose();
}
