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

        // Coverage uncertainty belongs in the explanatory status text, not in the compact
        // unlocked/total counter. The counter always means confirmed unlocks / known catalogue.
        _ = hasCompleteState;
        return $"{unlockedCount}/{knownCount}";
    }

    public static string ProgressText(
        int unlockedCount,
        int knownCount,
        bool hasCompleteCatalogue,
        bool hasCompleteState)
    {
        if (!hasCompleteCatalogue)
        {
            return unlockedCount == 1
                ? "1 confirmado · total desconocido"
                : $"{unlockedCount} confirmados · total desconocido";
        }

        if (knownCount <= 0)
        {
            return "Sin logros definidos";
        }

        if (unlockedCount >= knownCount)
        {
            return "100 % completado";
        }

        return hasCompleteState
            ? $"{unlockedCount}/{knownCount} · {unlockedCount * 100d / knownCount:0}%"
            : $"{unlockedCount}/{knownCount} confirmados · histórico incompleto";
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
