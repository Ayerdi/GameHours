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
        // provider can skip keyboard/mouse/controller idle inspection entirely. Active duration
        // deliberately remains zero because no AFK estimate exists for this session.
        if (idleThreshold == TimeSpan.Zero)
        {
            return new SessionActivityDelta(elapsed, TimeSpan.Zero);
        }

        var active = idleDuration >= TimeSpan.Zero && idleDuration < idleThreshold
            ? elapsed
            : TimeSpan.Zero;

        return new SessionActivityDelta(elapsed, active);
    }
}
