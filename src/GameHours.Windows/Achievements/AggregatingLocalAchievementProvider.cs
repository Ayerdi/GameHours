namespace GameHours.Windows.Achievements;

/// <summary>
/// Builds the richest local achievement snapshot available for a game.
/// Official Steam installations and Steam-compatible emulator saves are deliberately
/// isolated so state from two installations sharing an AppID is never mixed.
/// No network access is performed.
/// </summary>
public sealed class AggregatingLocalAchievementProvider : ILocalAchievementProvider
{
    private readonly GseAchievementReader _gseCatalogueReader = new();
    private readonly GseRuntimeAchievementStateReader _gseStateReader = new();
    private readonly SteamLocalStatsAchievementReader _steamStatsReader = new();
    private readonly SteamAchievementArtworkEnricher _steamArtworkEnricher = new();
    private readonly SteamAchievementMetadataCache _steamMetadataCache = new();
    private readonly SteamLibraryCacheAchievementReader _steamCacheReader = new();
    private readonly LocalAchievementSourceLocator _locator = new();
    private readonly SteamCompatibleAppIdResolver _appIdResolver = new();
    private readonly PartialAchievementStateReader _partialReader = new();

    public string Name => "Aggregated local achievements";

    public LocalAchievementSnapshot? TryRead(string executablePath) =>
        TryReadDetailed(executablePath).Snapshot;

    public AchievementReadResult TryReadDetailed(string executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return AchievementReadResult.NoSource(Name, "Executable path is empty.");
        }

        try
        {
            if (SteamLocalInstallation.TryResolve(executablePath) is not null)
            {
                var snapshot = ReadOfficialSteam(executablePath);
                return snapshot is null
                    ? AchievementReadResult.NoSource(Name)
                    : AchievementReadResult.Success(
                        Name,
                        snapshot,
                        AchievementStateCoverage.Unknown);
            }

            var nonSteam = ReadNonSteamLocal(executablePath);
            if (nonSteam.Snapshot is null)
            {
                return nonSteam.Diagnostics.Count == 0
                    ? AchievementReadResult.NoSource(Name)
                    : BuildFailure(nonSteam.Diagnostics);
            }

            return AchievementReadResult.Success(
                Name,
                nonSteam.Snapshot,
                nonSteam.HasState
                    ? AchievementStateCoverage.UnlocksOnly
                    : AchievementStateCoverage.Unknown,
                nonSteam.Diagnostics.Count == 0
                    ? AchievementSourceHealth.Healthy
                    : AchievementSourceHealth.Degraded,
                nonSteam.Diagnostics);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return AchievementReadResult.Failure(
                Name,
                AchievementReadStatus.Failed,
                AchievementSourceHealth.Degraded,
                exception.Message);
        }
    }

    private LocalAchievementSnapshot? ReadOfficialSteam(string executablePath)
    {
        var catalogue = _steamStatsReader.TryRead(executablePath);
        if (catalogue is not null)
        {
            catalogue = _steamArtworkEnricher.Enrich(catalogue);
        }

        var cacheState = _steamCacheReader.TryRead(executablePath) is { } cache
            ? cache with { IsCatalogueComplete = false }
            : null;

        if (catalogue is null)
        {
            return cacheState;
        }

        var states = new List<LocalAchievementSnapshot> { catalogue };
        if (cacheState is not null)
        {
            states.Add(cacheState);
        }

        return LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, states);
    }

    private NonSteamReadResult ReadNonSteamLocal(string executablePath)
    {
        var diagnostics = new List<AchievementReadDiagnostic>();
        var rawLocalCatalogue = _gseCatalogueReader.TryRead(executablePath);
        if (rawLocalCatalogue is null)
        {
            AddUnreadableGseCatalogueDiagnostic(executablePath, diagnostics);
        }

        var localCatalogue = AsCatalogueOnly(rawLocalCatalogue);
        var stateBatch = ReadEmulatorStates(executablePath);
        diagnostics.AddRange(stateBatch.Diagnostics);
        var states = stateBatch.States;
        var appId = localCatalogue?.AppId ??
                    states.Select(state => state.AppId)
                        .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        // Presentation metadata cached from Steam can act as a read-only catalogue for scene
        // emulator state (RUNE/CODEX/etc.). Do not replace a missing GSE catalogue this way:
        // GSE may require its own steam_settings definitions to record future unlocks, and the
        // desktop UI deliberately keeps its provisioning flow visible for that case.
        var catalogue = localCatalogue;
        if (catalogue is null &&
            !string.IsNullOrWhiteSpace(appId) &&
            !states.Any(IsGseState))
        {
            catalogue = _steamMetadataCache.TryReadCatalogueFromCache(appId);
        }

        LocalAchievementSnapshot? snapshot;
        if (catalogue is null)
        {
            snapshot = LocalAchievementSnapshotMerger.MergePartialStates(states);
        }
        else
        {
            var allStates = new List<LocalAchievementSnapshot> { catalogue };
            allStates.AddRange(states);
            snapshot = LocalAchievementSnapshotMerger.MergeCatalogueWithStates(catalogue, allStates);
        }

        // The provider remains local-only: this reads a previously fetched metadata cache.
        // Emulator files still own unlock state, timestamps and progress; Steam contributes presentation only.
        snapshot = snapshot is null ? null : _steamMetadataCache.EnrichFromCache(snapshot);
        return new NonSteamReadResult(snapshot, states.Count > 0, diagnostics);
    }

    private EmulatorStateReadBatch ReadEmulatorStates(string executablePath)
    {
        var states = new List<LocalAchievementSnapshot>();
        var diagnostics = new List<AchievementReadDiagnostic>();

        GseRuntimeAchievementStateLocation? gseLocation = null;
        try
        {
            gseLocation = GseRuntimeAchievementStateLocator.TryLocate(executablePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            diagnostics.Add(new AchievementReadDiagnostic(
                AchievementReadStatus.Failed,
                "GSE/Goldberg local state",
                SourcePath: null,
                exception.Message));
        }

        var gseState = _gseStateReader.TryRead(executablePath);
        if (gseState is not null)
        {
            states.Add(gseState);
        }
        else if (gseLocation is not null)
        {
            diagnostics.Add(new AchievementReadDiagnostic(
                AchievementReadStatus.Invalid,
                "GSE/Goldberg local state",
                gseLocation.FilePath,
                "The GSE/Goldberg state file exists, but the validated parser could not read it. It is not treated as an empty/locked state."));
        }

        IReadOnlyList<LocalAchievementSourceCandidate> candidates;
        try
        {
            var appIdHint = _appIdResolver.TryResolve(executablePath);
            candidates = _locator.Locate(executablePath, appIdHint)
                .Where(candidate => candidate.Kind is not LocalAchievementSourceKind.Goldberg)
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            diagnostics.Add(new AchievementReadDiagnostic(
                AchievementReadStatus.Failed,
                "Local achievement source locator",
                SourcePath: null,
                exception.Message));
            return new EmulatorStateReadBatch(states, diagnostics);
        }

        var candidateAppIds = candidates
            .Select(candidate => candidate.AppId)
            .Where(appId => !string.IsNullOrWhiteSpace(appId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (candidateAppIds.Length > 1)
        {
            diagnostics.Add(new AchievementReadDiagnostic(
                AchievementReadStatus.Ambiguous,
                "Local achievement source locator",
                SourcePath: null,
                $"Conflicting AppIDs were discovered for the same executable: {string.Join(", ", candidateAppIds)}. Partial emulator state was not merged."));
            return new EmulatorStateReadBatch(states, diagnostics);
        }

        foreach (var candidate in candidates)
        {
            var result = _partialReader.TryReadDetailed(candidate);
            if (result.IsSuccess && result.Snapshot is not null)
            {
                // An empty but valid state file matters: it establishes a baseline before the
                // first unlock and prevents that first future unlock from being treated as history.
                states.Add(result.Snapshot with { IsCatalogueComplete = false });
                continue;
            }

            if (result.Status != AchievementReadStatus.NoSource)
            {
                diagnostics.AddRange(result.Diagnostics);
            }
        }

        return new EmulatorStateReadBatch(states, diagnostics);
    }

    private static void AddUnreadableGseCatalogueDiagnostic(
        string executablePath,
        ICollection<AchievementReadDiagnostic> diagnostics)
    {
        try
        {
            var settingsDirectory = GseInstallationDetector.FindSettingsDirectory(executablePath);
            if (settingsDirectory is null)
            {
                return;
            }

            var definitionPath = Path.Combine(settingsDirectory, "achievements.json");
            if (File.Exists(definitionPath))
            {
                diagnostics.Add(new AchievementReadDiagnostic(
                    AchievementReadStatus.Invalid,
                    "GSE/Goldberg local catalogue",
                    definitionPath,
                    "The local achievement catalogue exists, but the validated parser could not read it."));
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or PathTooLongException)
        {
            diagnostics.Add(new AchievementReadDiagnostic(
                AchievementReadStatus.Failed,
                "GSE/Goldberg local catalogue",
                SourcePath: null,
                exception.Message));
        }
    }

    private AchievementReadResult BuildFailure(IReadOnlyList<AchievementReadDiagnostic> diagnostics)
    {
        var status = diagnostics.Any(item => item.Status == AchievementReadStatus.Ambiguous)
            ? AchievementReadStatus.Ambiguous
            : diagnostics.Any(item => item.Status == AchievementReadStatus.Invalid)
                ? AchievementReadStatus.Invalid
                : diagnostics.Any(item => item.Status == AchievementReadStatus.Failed)
                    ? AchievementReadStatus.Failed
                    : AchievementReadStatus.Unsupported;
        var health = status switch
        {
            AchievementReadStatus.Ambiguous => AchievementSourceHealth.Ambiguous,
            AchievementReadStatus.Invalid => AchievementSourceHealth.Invalid,
            AchievementReadStatus.Unsupported => AchievementSourceHealth.Unsupported,
            _ => AchievementSourceHealth.Degraded
        };

        return new AchievementReadResult(
            Name,
            status,
            health,
            AchievementStateCoverage.Unknown,
            Snapshot: null,
            diagnostics);
    }

    private static bool IsGseState(LocalAchievementSnapshot snapshot) =>
        snapshot.Source.Contains("GSE/Goldberg", StringComparison.OrdinalIgnoreCase);

    private static LocalAchievementSnapshot? AsCatalogueOnly(LocalAchievementSnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return null;
        }

        return snapshot with
        {
            StatePath = null,
            Achievements = snapshot.Achievements
                .Select(achievement => achievement with
                {
                    IsUnlocked = false,
                    UnlockedAtUtc = null,
                    Progress = null
                })
                .ToArray()
        };
    }

    private sealed record NonSteamReadResult(
        LocalAchievementSnapshot? Snapshot,
        bool HasState,
        IReadOnlyList<AchievementReadDiagnostic> Diagnostics);

    private sealed record EmulatorStateReadBatch(
        IReadOnlyList<LocalAchievementSnapshot> States,
        IReadOnlyList<AchievementReadDiagnostic> Diagnostics);
}
