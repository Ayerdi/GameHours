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

        var active = idleDuration >= TimeSpan.Zero && idleDuration < idleThreshold
            ? elapsed
            : TimeSpan.Zero;

        return new SessionActivityDelta(elapsed, active);
    }
}
