using GameHours.Core.Domain;
using GameHours.Core.Timeline;

namespace GameHours.Tests;

public sealed class SrumGapRecoveryEvidenceFactoryTests
{
    private static readonly Guid GameId = Guid.Parse("d557b6f7-1d58-4b31-b2b8-067940db9a71");
    private static readonly DateTimeOffset Cutover = DateTimeOffset.Parse("2026-08-20T18:00:00Z");

    [Fact]
    public void Create_UsesEstimatedForegroundGapRecoveryAndHourlyCoverage()
    {
        var evidence = SrumGapRecoveryEvidenceFactory.Create(
            GameId,
            Cutover.AddHours(4),
            TimeSpan.FromMinutes(22),
            Cutover);

        Assert.Equal(HistoricalSource.Srum, evidence.Source);
        Assert.Equal(EvidenceKind.GapRecovery, evidence.Kind);
        Assert.Equal(PlaytimeMetric.Foreground, evidence.Metric);
        Assert.Equal(Confidence.Estimated, evidence.Confidence);
        Assert.Equal(Cutover.AddHours(3), evidence.PeriodStartUtc);
        Assert.Equal(Cutover.AddHours(4), evidence.PeriodEndUtc);
        Assert.Equal(TimeSpan.FromMinutes(22), evidence.Duration);
    }

    [Fact]
    public void Create_IsDeterministicForTheSameSample()
    {
        var first = SrumGapRecoveryEvidenceFactory.Create(
            GameId,
            Cutover.AddHours(4),
            TimeSpan.FromMinutes(22),
            Cutover);
        var second = SrumGapRecoveryEvidenceFactory.Create(
            GameId,
            Cutover.AddHours(4),
            TimeSpan.FromMinutes(22),
            Cutover);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Create_ClampsUncertaintyWindowAtCutoverWhenDurationStillFits()
    {
        var evidence = SrumGapRecoveryEvidenceFactory.Create(
            GameId,
            Cutover.AddMinutes(45),
            TimeSpan.FromMinutes(20),
            Cutover);

        Assert.Equal(Cutover, evidence.PeriodStartUtc);
        Assert.Equal(Cutover.AddMinutes(45), evidence.PeriodEndUtc);
        Assert.Equal(TimeSpan.FromMinutes(20), evidence.Duration);
    }

    [Fact]
    public void Create_RejectsSampleWhoseDurationWouldNeedPreCutoverTime()
    {
        Assert.Throws<TimelineConflictException>(() =>
            SrumGapRecoveryEvidenceFactory.Create(
                GameId,
                Cutover.AddMinutes(30),
                TimeSpan.FromMinutes(45),
                Cutover));
    }

    [Fact]
    public void Create_ExpandsCoverageWhenDurationExceedsNominalBucket()
    {
        var evidence = SrumGapRecoveryEvidenceFactory.Create(
            GameId,
            Cutover.AddHours(5),
            TimeSpan.FromMinutes(75),
            Cutover);

        Assert.Equal(Cutover.AddHours(3).AddMinutes(45), evidence.PeriodStartUtc);
        Assert.Equal(TimeSpan.FromMinutes(75), evidence.Duration);
    }
}
