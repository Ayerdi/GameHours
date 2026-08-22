namespace GameHours.Core.Tracking;

public readonly record struct SessionActivityDelta(
    TimeSpan FocusedDuration,
    TimeSpan ActiveDuration);

public static class SessionActivityPolicy
{
    public static SessionActivityDelta Measure(
        TimeSpan elapsed,
        bool isFocused,
        TimeSpan idleDuration,
        TimeSpan idleThreshold,
        TimeSpan maximumSampleGap)
    {
        if (elapsed <= TimeSpan.Zero ||
            elapsed > maximumSampleGap ||
            !isFocused)
        {
            return default;
        }

        // A zero threshold means the AFK filter is disabled. Focus remains observable while the
        // provider can skip keyboard/mouse/controller idle inspection entirely; active then
        // intentionally mirrors focused time instead of pretending an AFK estimate exists.
        if (idleThreshold == TimeSpan.Zero)
        {
            return new SessionActivityDelta(elapsed, elapsed);
        }

        var active = idleDuration >= TimeSpan.Zero && idleDuration < idleThreshold
            ? elapsed
            : TimeSpan.Zero;

        return new SessionActivityDelta(elapsed, active);
    }
}
