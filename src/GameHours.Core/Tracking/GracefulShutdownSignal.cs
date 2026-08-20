namespace GameHours.Core.Tracking;

/// <summary>
/// Process-local lifecycle signal used by hosts (console, tray UI, updater coordinator)
/// to request an intentional tracker shutdown. It is deliberately event-based rather
/// than a permanently cancelled token so a desktop host can stop and later restart the
/// tracker within the same process.
/// </summary>
public static class GracefulShutdownSignal
{
    public static event Action? Requested;

    public static void Request() => Requested?.Invoke();
}
