namespace GameHours.Desktop;

internal static class AchievementPresentation
{
    public static string CountText(
        int? unlockedCount,
        int? knownCount,
        bool hasCompleteCatalogue,
        bool hasCompleteState)
    {
        if (unlockedCount is null)
        {
            return "—";
        }

        if (!hasCompleteCatalogue || knownCount is null)
        {
            return unlockedCount == 1
                ? "1 confirmado"
                : $"{unlockedCount} confirmados";
        }

        if (hasCompleteState)
        {
            return $"{unlockedCount}/{knownCount}";
        }

        return unlockedCount > 0
            ? $"{unlockedCount}+/{knownCount}"
            : $"?/{knownCount}";
    }

    public static string TimelineText(
        string? displayName,
        string? apiName,
        string? description)
    {
        var name = Normalize(
            string.IsNullOrWhiteSpace(displayName)
                ? apiName
                : displayName);
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "Logro desbloqueado";
        }

        var detail = Normalize(description);
        return string.IsNullOrWhiteSpace(detail)
            ? name
            : $"{name} — {detail}";
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            value.Split(
                new[] { '\r', '\n', '\t' },
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }
}
