namespace GameHours.Desktop;

public sealed partial class DesktopHost
{
    public int? AppliedAfkTimeoutMinutes =>
        IsTrackerRunning
            ? Volatile.Read(ref _appliedAfkTimeoutMinutes)
            : null;
}
