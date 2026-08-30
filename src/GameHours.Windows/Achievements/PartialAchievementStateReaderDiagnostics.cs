namespace GameHours.Windows.Achievements;

/// <summary>
/// Adds structured diagnostics around legacy partial-state parsers without changing their
/// parsing contract. A source that exists but cannot be parsed is no longer indistinguishable
/// from a source that was never present.
/// </summary>
public static class PartialAchievementStateReaderDiagnostics
{
    public static AchievementReadResult TryReadDetailed(
        this PartialAchievementStateReader reader,
        LocalAchievementSourceCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(candidate);

        var provider = $"{candidate.Kind} local state";
        if (!reader.Supports(candidate.Kind))
        {
            return AchievementReadResult.Failure(
                provider,
                AchievementReadStatus.Unsupported,
                AchievementSourceHealth.Unsupported,
                $"{candidate.Kind} is recognized by discovery but does not have a validated parser.",
                candidate.FilePath);
        }

        if (!File.Exists(candidate.FilePath))
        {
            return AchievementReadResult.NoSource(
                provider,
                "The discovered achievement state file no longer exists.",
                candidate.FilePath);
        }

        var snapshot = reader.TryRead(candidate);
        if (snapshot is null)
        {
            return AchievementReadResult.Failure(
                provider,
                AchievementReadStatus.Invalid,
                AchievementSourceHealth.Invalid,
                "The local achievement state file exists, but the validated parser could not read it. The file is not treated as an empty/locked state.",
                candidate.FilePath);
        }

        return AchievementReadResult.Success(
            provider,
            snapshot,
            AchievementStateCoverage.UnlocksOnly);
    }
}
