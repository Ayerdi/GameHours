namespace GameHours.Desktop;

internal sealed record AchievementNotificationContent(
    string Title,
    IReadOnlyList<string> BodyLines);

internal static class AchievementNotificationPresentation
{
    public static AchievementNotificationContent Build(DesktopAchievementUnlocked notice)
    {
        ArgumentNullException.ThrowIfNull(notice);
        var name = Normalize(string.IsNullOrWhiteSpace(notice.Achievement.DisplayName)
            ? notice.Achievement.ApiName
            : notice.Achievement.DisplayName);
        var lines = new List<string> { name };
        var description = Normalize(notice.Achievement.Description);
        if (!string.IsNullOrWhiteSpace(description)) lines.Add(description);
        var game = Normalize(notice.GameTitle);
        if (!string.IsNullOrWhiteSpace(game)) lines.Add(game);

        return new AchievementNotificationContent("Logro desbloqueado", lines);
    }

    internal static string Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : string.Join(" ", value.Split(
            ['\r', '\n', '\t'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}
