using GameHours.Core.Monitoring;

namespace GameHours.Tests;

public sealed class SystemSleepGapDetectorTests
{
    [Fact]
    public void EqualBiasedAndUnbiasedAdvanceDoesNotReportSleep()
    {
        var detector = new SystemSleepGapDetector(TimeSpan.FromSeconds(2));
        var at = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        Assert.Null(detector.Observe(new SystemUptimeSample(
            at,
            TimeSpan.FromHours(10),
            TimeSpan.FromHours(9))));

        var result = detector.Observe(new SystemUptimeSample(
            at.AddMinutes(30),
            TimeSpan.FromHours(10.5),
            TimeSpan.FromHours(9.5)));

        Assert.Null(result);
    }

    [Fact]
    public void BiasedAdvanceBeyondUnbiasedReportsConservativeSleepGap()
    {
        var detector = new SystemSleepGapDetector(TimeSpan.FromSeconds(2));
        var beforeSleep = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);
        var afterResume = beforeSleep.AddMinutes(10).AddSeconds(2);

        detector.Observe(new SystemUptimeSample(
            beforeSleep,
            TimeSpan.FromHours(10),
            TimeSpan.FromHours(9)));

        var result = detector.Observe(new SystemUptimeSample(
            afterResume,
            TimeSpan.FromHours(10).Add(TimeSpan.FromMinutes(10)).Add(TimeSpan.FromSeconds(2)),
            TimeSpan.FromHours(9).Add(TimeSpan.FromSeconds(2))));

        Assert.NotNull(result);
        Assert.Equal(beforeSleep, result!.SuspendedAtUtc);
        Assert.Equal(afterResume, result.ResumedAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), result.SleepDuration);
    }

    [Fact]
    public void CounterRollbackResetsBaselineInsteadOfReportingSleep()
    {
        var detector = new SystemSleepGapDetector(TimeSpan.FromSeconds(2));
        var at = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero);

        detector.Observe(new SystemUptimeSample(
            at,
            TimeSpan.FromHours(10),
            TimeSpan.FromHours(9)));

        var reset = detector.Observe(new SystemUptimeSample(
            at.AddMinutes(1),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(25)));

        Assert.Null(reset);

        var afterReset = detector.Observe(new SystemUptimeSample(
            at.AddMinutes(2),
            TimeSpan.FromSeconds(90),
            TimeSpan.FromSeconds(85)));

        Assert.Null(afterReset);
    }
}
