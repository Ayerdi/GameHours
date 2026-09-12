namespace GameHours.Desktop;

internal enum DesktopGameHealthState
{
    Ready,
    NeedsAttention,
    NotTracking
}

internal enum DesktopGameHealthCheckState
{
    Ready,
    NeedsAttention,
    Informational
}

internal sealed record DesktopGameHealthCheck(
    string Code,
    string Title,
    string Detail,
    DesktopGameHealthCheckState State);

internal sealed record DesktopGameHealthSnapshot(
    Guid GameId,
    DesktopGameHealthState OverallState,
    string Summary,
    IReadOnlyList<DesktopGameHealthCheck> Checks,
    DateTimeOffset ObservedAtUtc);

internal static class DesktopGameHealthSnapshotBuilder
{
    public static DesktopGameHealthSnapshot Build(
        DesktopGameRow game,
        bool isTracking,
        bool isActive,
        DateTimeOffset observedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(game);

        var executablePath = game.ExecutablePath;
        var hasExecutableAssociation = !string.IsNullOrWhiteSpace(executablePath);
        var executableExists = hasExecutableAssociation && game.ExecutableExists == true;

        var overallState = !isTracking
            ? DesktopGameHealthState.NotTracking
            : isActive
                ? DesktopGameHealthState.Ready
                : hasExecutableAssociation && !executableExists
                ? DesktopGameHealthState.NeedsAttention
                : DesktopGameHealthState.Ready;

        var summary = overallState switch
        {
            DesktopGameHealthState.NotTracking =>
                "El seguimiento de GameHours está detenido y no se medirán nuevas sesiones mientras siga así.",
            DesktopGameHealthState.NeedsAttention =>
                "El ejecutable aprendido ya no se encuentra en su ruta guardada. GameHours tendrá que volver a resolverlo cuando detecte el juego.",
            _ when isActive =>
                "GameHours reconoce el juego y está siguiendo una sesión activa ahora mismo.",
            _ when !hasExecutableAssociation =>
                "No hay problemas conocidos en el seguimiento. Todavía no hay un ejecutable aprendido para este juego.",
            _ =>
                "GameHours reconoce el juego y está listo para medir la próxima sesión."
        };

        var checks = new List<DesktopGameHealthCheck>
        {
            new(
                "identity",
                "Identidad del juego",
                $"Identidad local registrada para {game.Title}.",
                DesktopGameHealthCheckState.Ready),
            BuildExecutableCheck(executablePath, hasExecutableAssociation, executableExists, isActive),
            BuildTrackingCheck(isTracking, isActive),
            BuildMeasuredHistoryCheck(game),
            BuildHistoricalRecoveryCheck(game),
            BuildAchievementCheck(game)
        };

        return new DesktopGameHealthSnapshot(
            game.GameId,
            overallState,
            summary,
            checks,
            observedAtUtc.ToUniversalTime());
    }

    private static DesktopGameHealthCheck BuildExecutableCheck(
        string? executablePath,
        bool hasAssociation,
        bool exists,
        bool isActive)
    {
        if (!hasAssociation)
        {
            return new(
                "executable",
                "Ejecutable",
                "Todavía no hay un ejecutable aprendido. GameHours puede resolverlo cuando detecte el juego.",
                DesktopGameHealthCheckState.Informational);
        }

        if (exists)
        {
            return new DesktopGameHealthCheck(
                "executable",
                "Ejecutable",
                $"Ejecutable reconocido: {executablePath}",
                DesktopGameHealthCheckState.Ready);
        }

        if (isActive)
        {
            return new DesktopGameHealthCheck(
                "executable",
                "Ejecutable",
                $"La ruta aprendida ya no se encuentra ({executablePath}), pero la sesión actual confirma que GameHours está resolviendo el juego.",
                DesktopGameHealthCheckState.Informational);
        }

        return new DesktopGameHealthCheck(
            "executable",
            "Ejecutable",
            $"La ruta aprendida ya no se encuentra: {executablePath}",
            DesktopGameHealthCheckState.NeedsAttention);
    }

    private static DesktopGameHealthCheck BuildTrackingCheck(bool isTracking, bool isActive)
    {
        if (!isTracking)
        {
            return new(
                "tracking",
                "Seguimiento",
                "El tracker de GameHours está detenido.",
                DesktopGameHealthCheckState.NeedsAttention);
        }

        return new(
            "tracking",
            "Seguimiento",
            isActive
                ? "GameHours está midiendo una sesión de este juego ahora mismo."
                : "El tracker está activo y preparado para detectar la próxima sesión.",
            DesktopGameHealthCheckState.Ready);
    }

    private static DesktopGameHealthCheck BuildMeasuredHistoryCheck(DesktopGameRow game)
    {
        if (game.MeasuredSessionCount == 0)
        {
            return new(
                "measured-history",
                "Sesiones medidas",
                "Todavía no hay sesiones medidas por GameHours. Esto es normal en un juego nuevo.",
                DesktopGameHealthCheckState.Informational);
        }

        var lastMeasured = game.LastMeasuredSessionAtUtc is DateTimeOffset last
            ? $" Última sesión: {last.ToLocalTime():g}."
            : string.Empty;
        var countText = game.MeasuredSessionCount == 1
            ? "1 sesión medida guardada localmente."
            : $"{game.MeasuredSessionCount} sesiones medidas guardadas localmente.";
        return new(
            "measured-history",
            "Sesiones medidas",
            countText + lastMeasured,
            DesktopGameHealthCheckState.Ready);
    }

    private static DesktopGameHealthCheck BuildHistoricalRecoveryCheck(DesktopGameRow game) =>
        game.EstimatedPlaytime > TimeSpan.Zero
            ? new(
                "historical-recovery",
                "Histórico recuperado",
                "Hay evidencia histórica recuperada y se mantiene separada del tiempo medido.",
                DesktopGameHealthCheckState.Informational)
            : new(
                "historical-recovery",
                "Histórico recuperado",
                "No hay histórico recuperado. Esto no afecta a la medición de nuevas sesiones.",
                DesktopGameHealthCheckState.Informational);

    private static DesktopGameHealthCheck BuildAchievementCheck(DesktopGameRow game)
    {
        if (game.AchievementKnownCount is not int knownCount)
        {
            return new(
                "achievements",
                "Logros locales",
                "No hay datos de logros persistidos para este juego. El seguimiento del tiempo funciona de forma independiente.",
                DesktopGameHealthCheckState.Informational);
        }

        var unlockedCount = game.AchievementUnlockedCount ?? 0;
        var coverageText = game.AchievementHasCompleteCatalogue
            ? $"{unlockedCount} de {knownCount} logros conocidos."
            : $"{unlockedCount} desbloqueos confirmados; el catálogo local no está completo.";
        return new(
            "achievements",
            "Logros locales",
            coverageText,
            DesktopGameHealthCheckState.Informational);
    }
}
