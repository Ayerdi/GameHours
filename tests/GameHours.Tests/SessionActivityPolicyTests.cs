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
        Assert.Zero(delta.ActiveDuration);
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

        Assert.Zero(delta.FocusedDuration);
        Assert.Zero(delta.ActiveDuration);
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

        Assert.Zero(delta.FocusedDuration);
        Assert.Zero(delta.ActiveDuration);
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
        Assert.Zero(delta.ActiveDuration);
    }
}
