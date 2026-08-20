namespace GameHours.Core.Monitoring;

public sealed record SystemUptimeSample(
    DateTimeOffset ObservedAtUtc,
    TimeSpan BiasedUptime,
    TimeSpan UnbiasedUptime);

public sealed record SystemSleepGap(
    DateTimeOffset SuspendedAtUtc,
    DateTimeOffset ResumedAtUtc,
    TimeSpan SleepDuration);

/// <summary>
/// Detects system sleep by comparing a clock that advances through sleep with one that only
/// advances while Windows is in the working state. Detection deliberately does not depend on
/// wall-clock deltas, so NTP/user clock adjustments cannot by themselves create a sleep gap.
/// </summary>
public sealed class SystemSleepGapDetector
{
    private readonly TimeSpan _minimumSleepDuration;
    private SystemUptimeSample? _previous;

    public SystemSleepGapDetector(TimeSpan? minimumSleepDuration = null)
    {
        _minimumSleepDuration = minimumSleepDuration ?? TimeSpan.FromSeconds(2);
        if (_minimumSleepDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minimumSleepDuration));
        }
    }

    public SystemSleepGap? Observe(SystemUptimeSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);
        sample = sample with { ObservedAtUtc = sample.ObservedAtUtc.ToUniversalTime() };

        var previous = _previous;
        _previous = sample;
        if (previous is null)
        {
            return null;
        }

        var biasedDelta = sample.BiasedUptime - previous.BiasedUptime;
        var unbiasedDelta = sample.UnbiasedUptime - previous.UnbiasedUptime;

        // Counter rollback means a reboot/provider reset. A backwards wall clock would also
        // make the conservative stop/start boundaries invalid. Establish a new baseline rather
        // than inventing a sleep interval in either case.
        if (biasedDelta < TimeSpan.Zero ||
            unbiasedDelta < TimeSpan.Zero ||
            sample.ObservedAtUtc <= previous.ObservedAtUtc)
        {
            return null;
        }

        var sleepDuration = biasedDelta - unbiasedDelta;
        if (sleepDuration < _minimumSleepDuration)
        {
            return null;
        }

        // We only discover the sleep after the system has resumed. Use the last pre-sleep poll
        // and first post-resume poll as conservative boundaries: this can discard at most one
        // polling interval of legitimate awake play on either side, but it never counts the
        // actual sleep interval as playtime.
        return new SystemSleepGap(
            previous.ObservedAtUtc,
            sample.ObservedAtUtc,
            sleepDuration);
    }
}
