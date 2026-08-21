namespace GameHours.Windows.Achievements;

/// <summary>
/// Enriches an official Steam achievement catalogue with exact local artwork paths derived
/// from the schema's icon/icon_gray asset names. Missing local assets remain null; no network
/// access or filename guessing is performed.
/// </summary>
public sealed class SteamAchievementArtworkEnricher
{
    public LocalAchievementSnapshot Enrich(LocalAchievementSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (string.IsNullOrWhiteSpace(snapshot.AppId) ||
            !snapshot.AppId.All(char.IsDigit) ||
            string.IsNullOrWhiteSpace(snapshot.DefinitionPath) ||
            !File.Exists(snapshot.DefinitionPath))
        {
            return snapshot;
        }

        try
        {
            var root = BinaryKeyValueReader.TryRead(snapshot.DefinitionPath);
            var appNode = root?.Child(snapshot.AppId) ?? root?.FindFirst(snapshot.AppId);
            var statsNode = appNode?.Child("stats");
            if (statsNode is null)
            {
                return snapshot;
            }

            var assets = new Dictionary<string, AchievementArtworkNames>(
                StringComparer.OrdinalIgnoreCase);
            foreach (var group in statsNode.Children)
            {
                var bits = group.Child("bits");
                if (bits is null)
                {
                    continue;
                }

                foreach (var bit in bits.Children)
                {
                    var apiName = bit.Child("name")?.AsString();
                    if (string.IsNullOrWhiteSpace(apiName))
                    {
                        continue;
                    }

                    var display = bit.Child("display");
                    assets[apiName] = new AchievementArtworkNames(
                        display?.Child("icon")?.AsString(),
                        display?.Child("icon_gray")?.AsString());
                }
            }

            if (assets.Count == 0)
            {
                return snapshot;
            }

            var steamRoot = SteamAchievementIconResolver.TryInferSteamRootFromSchemaPath(
                snapshot.DefinitionPath);
            if (string.IsNullOrWhiteSpace(steamRoot))
            {
                return snapshot;
            }

            var enriched = snapshot.Achievements
                .Select(achievement =>
                {
                    if (!assets.TryGetValue(achievement.ApiName, out var names))
                    {
                        return achievement;
                    }

                    var iconPath = SteamAchievementIconResolver.TryResolve(
                        steamRoot,
                        snapshot.AppId,
                        names.UnlockedAssetName);
                    var lockedIconPath = SteamAchievementIconResolver.TryResolve(
                        steamRoot,
                        snapshot.AppId,
                        names.LockedAssetName);

                    if (iconPath is null && lockedIconPath is null)
                    {
                        return achievement;
                    }

                    return achievement with
                    {
                        IconPath = iconPath ?? achievement.IconPath,
                        LockedIconPath = lockedIconPath ?? achievement.LockedIconPath
                    };
                })
                .ToArray();

            return snapshot with { Achievements = enriched };
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or
            InvalidOperationException or FormatException or PathTooLongException)
        {
            return snapshot;
        }
    }

    private sealed record AchievementArtworkNames(
        string? UnlockedAssetName,
        string? LockedAssetName);
}
