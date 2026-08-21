namespace GameHours.Windows.Achievements;

public static class LocalAchievementSnapshotMerger
{
    public static LocalAchievementSnapshot MergeCatalogueWithStates(
        LocalAchievementSnapshot catalogue,
        IEnumerable<LocalAchievementSnapshot> stateSnapshots)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(stateSnapshots);

        var states = stateSnapshots
            .Where(snapshot => snapshot is not null)
            .ToArray();

        var stateByName = states
            .SelectMany(snapshot => snapshot.Achievements)
            .Where(achievement => achievement.IsUnlocked)
            .GroupBy(achievement => achievement.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                MergeUnlockedEntries,
                StringComparer.OrdinalIgnoreCase);

        var mergedAchievements = catalogue.Achievements
            .Select(definition =>
            {
                if (!stateByName.TryGetValue(definition.ApiName, out var externalState))
                {
                    return definition;
                }

                var unlockedAt = EarliestTimestamp(
                    definition.IsUnlocked ? definition.UnlockedAtUtc : null,
                    externalState.UnlockedAtUtc);

                return definition with
                {
                    IsUnlocked = definition.IsUnlocked || externalState.IsUnlocked,
                    UnlockedAtUtc = unlockedAt,
                    Progress = MaxNullable(definition.Progress, externalState.Progress),
                    MaxProgress = MaxNullable(definition.MaxProgress, externalState.MaxProgress)
                };
            })
            .ToArray();

        var contributingStates = states
            .Where(snapshot => snapshot.UnlockedCount > 0)
            .ToArray();
        var statePath = catalogue.StatePath
            ?? contributingStates.Select(snapshot => snapshot.StatePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return catalogue with
        {
            Source = BuildCompleteSource(catalogue, contributingStates),
            StatePath = statePath,
            Achievements = mergedAchievements,
            IsCatalogueComplete = true
        };
    }

    public static LocalAchievementSnapshot? MergePartialStates(
        IEnumerable<LocalAchievementSnapshot> stateSnapshots)
    {
        ArgumentNullException.ThrowIfNull(stateSnapshots);

        var states = stateSnapshots
            .Where(snapshot => snapshot is not null && snapshot.UnlockedCount > 0)
            .ToArray();
        if (states.Length == 0)
        {
            return null;
        }

        var achievements = states
            .SelectMany(snapshot => snapshot.Achievements)
            .Where(achievement => achievement.IsUnlocked)
            .GroupBy(achievement => achievement.ApiName, StringComparer.OrdinalIgnoreCase)
            .Select(MergeUnlockedEntries)
            .OrderBy(achievement => achievement.ApiName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (achievements.Length == 0)
        {
            return null;
        }

        var first = states[0];
        return new LocalAchievementSnapshot(
            BuildPartialSource(states),
            states.Select(snapshot => snapshot.AppId).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)),
            first.DefinitionPath,
            states.Select(snapshot => snapshot.StatePath).FirstOrDefault(path => !string.IsNullOrWhiteSpace(path)),
            achievements)
        {
            IsCatalogueComplete = false
        };
    }

    private static LocalAchievement MergeUnlockedEntries(IGrouping<string, LocalAchievement> group)
    {
        var entries = group.ToArray();
        var preferred = entries
            .OrderByDescending(HasRichMetadata)
            .ThenBy(achievement => achievement.UnlockedAtUtc ?? DateTimeOffset.MaxValue)
            .First();

        return preferred with
        {
            IsUnlocked = true,
            UnlockedAtUtc = EarliestTimestamp(entries.Select(item => item.UnlockedAtUtc).ToArray()),
            Progress = entries.Select(item => item.Progress).Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty().Max() is var progress && progress > 0
                ? progress
                : preferred.Progress,
            MaxProgress = entries.Select(item => item.MaxProgress).Where(value => value is not null).Select(value => value!.Value).DefaultIfEmpty().Max() is var maxProgress && maxProgress > 0
                ? maxProgress
                : preferred.MaxProgress
        };
    }

    private static bool HasRichMetadata(LocalAchievement achievement) =>
        !string.Equals(achievement.DisplayName, achievement.ApiName, StringComparison.OrdinalIgnoreCase) ||
        !string.IsNullOrWhiteSpace(achievement.Description) ||
        !string.IsNullOrWhiteSpace(achievement.IconPath) ||
        !string.IsNullOrWhiteSpace(achievement.LockedIconPath);

    private static DateTimeOffset? EarliestTimestamp(params DateTimeOffset?[] values)
    {
        var timestamps = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return timestamps.Length == 0 ? null : timestamps.Min();
    }

    private static long? MaxNullable(long? left, long? right)
    {
        if (left is null)
        {
            return right;
        }

        if (right is null)
        {
            return left;
        }

        return Math.Max(left.Value, right.Value);
    }

    private static string BuildCompleteSource(
        LocalAchievementSnapshot catalogue,
        IReadOnlyList<LocalAchievementSnapshot> contributingStates)
    {
        if (contributingStates.Count == 0)
        {
            return catalogue.StatePath is null
                ? "Catálogo local de logros"
                : catalogue.Source;
        }

        var externalSources = contributingStates
            .Where(snapshot => !ReferenceEquals(snapshot, catalogue))
            .Select(snapshot => NormalizeSourceName(snapshot.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (externalSources.Length == 0)
        {
            return catalogue.Source;
        }

        return $"Catálogo local + {string.Join(" + ", externalSources)}";
    }

    private static string BuildPartialSource(IReadOnlyList<LocalAchievementSnapshot> states)
    {
        var names = states
            .Select(snapshot => NormalizeSourceName(snapshot.Source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return names.Length == 1
            ? $"{names[0]} · estado parcial"
            : $"{string.Join(" + ", names)} · estado parcial";
    }

    private static string NormalizeSourceName(string source)
    {
        const string partialSuffix = " · estado parcial";
        return source.EndsWith(partialSuffix, StringComparison.OrdinalIgnoreCase)
            ? source[..^partialSuffix.Length]
            : source;
    }
}
