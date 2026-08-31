using GameHours.Core.Tracking;

namespace GameHours.Tests;

public sealed class SessionActivityPolicyTests
{
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxGap = TimeSpan.FromSeconds(3);

    [Fact]
    public void FocusedWithRecentInput_CountsFocusedAndActive()
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromSeconds(1),
            isFocused: true,
            idleDuration: TimeSpan.FromSeconds(10),
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.FromSeconds(1), delta.FocusedDuration);
        Assert.Equal(TimeSpan.FromSeconds(1), delta.ActiveDuration);
    }

    [Fact]
    public void FocusedButIdle_CountsOnlyFocused()
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromSeconds(1),
            isFocused: true,
            idleDuration: IdleThreshold,
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.FromSeconds(1), delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }

    [Fact]
    public void DisabledAfkFilter_CountsFocusWithoutFabricatingActiveEstimate()
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromSeconds(1),
            isFocused: true,
            idleDuration: TimeSpan.MaxValue,
            idleThreshold: TimeSpan.Zero,
            MaxGap);

        Assert.Equal(TimeSpan.FromSeconds(1), delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }

    [Fact]
    public void NotFocused_CountsNeitherMetric()
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromSeconds(1),
            isFocused: false,
            idleDuration: TimeSpan.Zero,
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.Zero, delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }

    [Fact]
    public void LongSamplingGap_IsUnknownInsteadOfFabricatedAsActivity()
    {
        var delta = SessionActivityPolicy.Measure(
            MaxGap + TimeSpan.FromMilliseconds(1),
            isFocused: true,
            idleDuration: TimeSpan.Zero,
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.Zero, delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }

    [Fact]
    public void NegativeIdleDuration_NeverCountsAsActive()
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromSeconds(1),
            isFocused: true,
            idleDuration: TimeSpan.FromSeconds(-1),
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.FromSeconds(1), delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }

    [Theory]
    [InlineData(119_999, true)]
    [InlineData(120_000, false)]
    [InlineData(120_001, false)]
    public void TwoMinuteAfkBoundary_IsActiveOnlyStrictlyBelowThreshold(
        int idleMilliseconds,
        bool expectedActive)
    {
        var elapsed = TimeSpan.FromSeconds(1);
        var delta = SessionActivityPolicy.Measure(
            elapsed,
            isFocused: true,
            idleDuration: TimeSpan.FromMilliseconds(idleMilliseconds),
            idleThreshold: TimeSpan.FromMinutes(2),
            MaxGap);

        Assert.Equal(elapsed, delta.FocusedDuration);
        Assert.Equal(expectedActive ? elapsed : TimeSpan.Zero, delta.ActiveDuration);
    }

    [Fact]
    public void ForegroundBackgroundForeground_AccumulatesOnlyObservedFocusedIntervals()
    {
        var intervals = new[]
        {
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: true,
                idleDuration: TimeSpan.Zero,
                idleThreshold: TimeSpan.FromMinutes(2),
                MaxGap),
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: false,
                idleDuration: TimeSpan.Zero,
                idleThreshold: TimeSpan.FromMinutes(2),
                MaxGap),
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: true,
                idleDuration: TimeSpan.Zero,
                idleThreshold: TimeSpan.FromMinutes(2),
                MaxGap)
        };

        var focused = TimeSpan.FromTicks(intervals.Sum(item => item.FocusedDuration.Ticks));
        var active = TimeSpan.FromTicks(intervals.Sum(item => item.ActiveDuration.Ticks));

        Assert.Equal(TimeSpan.FromSeconds(2), focused);
        Assert.Equal(TimeSpan.FromSeconds(2), active);
    }

    [Fact]
    public void InputRecoveryAfterAfk_ResumesOnlySubsequentActiveIntervals()
    {
        var threshold = TimeSpan.FromMinutes(2);
        var intervals = new[]
        {
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: true,
                idleDuration: threshold - TimeSpan.FromMilliseconds(1),
                threshold,
                MaxGap),
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: true,
                idleDuration: threshold,
                threshold,
                MaxGap),
            SessionActivityPolicy.Measure(
                TimeSpan.FromSeconds(1),
                isFocused: true,
                idleDuration: TimeSpan.Zero,
                threshold,
                MaxGap)
        };

        var focused = TimeSpan.FromTicks(intervals.Sum(item => item.FocusedDuration.Ticks));
        var active = TimeSpan.FromTicks(intervals.Sum(item => item.ActiveDuration.Ticks));

        Assert.Equal(TimeSpan.FromSeconds(3), focused);
        Assert.Equal(TimeSpan.FromSeconds(2), active);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveElapsed_NeverProducesNegativeOrFabricatedDurations(int elapsedMilliseconds)
    {
        var delta = SessionActivityPolicy.Measure(
            TimeSpan.FromMilliseconds(elapsedMilliseconds),
            isFocused: true,
            idleDuration: TimeSpan.Zero,
            IdleThreshold,
            MaxGap);

        Assert.Equal(TimeSpan.Zero, delta.FocusedDuration);
        Assert.Equal(TimeSpan.Zero, delta.ActiveDuration);
    }
}
