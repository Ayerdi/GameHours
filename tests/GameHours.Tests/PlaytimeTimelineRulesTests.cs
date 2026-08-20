using GameHours.Core.Domain;
using GameHours.Core.Timeline;

namespace GameHours.Tests;

public sealed class PlaytimeTimelineRulesTests
{
    private static readonly Guid GameId = Guid.NewGuid();
    private static readonly DateTimeOffset Cutover =
        DateTimeOffset.Parse("2026-08-20T18:00:00Z");

    [Fact]
    public void MeasuredSession_CannotClaimTimeBeforeCutover()
    {
        var session = new PlaySession(
            Guid.NewGuid(),
            GameId,
            Cutover.AddSeconds(-1),
            Cutover.AddMinutes(1),
            CaptureMethod.InitialSnapshot,
            Confidence.High);

        Assert.Throws<TimelineConflictException>(() =>
            PlaytimeTimelineRules.ValidateMeasuredSession(session, Cutover));
    }

    [Fact]
    public void Baseline_CanEndExactlyAtCutover()
    {
        var evidence = Evidence(
            EvidenceKind.Baseline,
            Cutover.AddDays(-10),
            Cutover,
            TimeSpan.FromHours(53));

        PlaytimeTimelineRules.ValidateAgainstCutover(evidence, Cutover);
    }

    [Fact]
    public void Baseline_CannotExtendPastCutover()
    {
        var evidence = Evidence(
            EvidenceKind.Baseline,
            Cutover.AddDays(-10),
            Cutover.AddSeconds(1),
            TimeSpan.FromHours(53));

        Assert.Throws<TimelineConflictException>(() =>
            PlaytimeTimelineRules.ValidateAgainstCutover(evidence, Cutover));
    }

    [Fact]
    public void GapRecovery_CannotStartBeforeCutover()
    {
        var evidence = Evidence(
            EvidenceKind.GapRecovery,
            Cutover.AddSeconds(-1),
            Cutover.AddHours(2),
            TimeSpan.FromHours(1));

        Assert.Throws<TimelineConflictException>(() =>
            PlaytimeTimelineRules.ValidateAgainstCutover(evidence, Cutover));
    }

    [Theory]
    [InlineData("2026-08-20T18:00:00Z", "2026-08-20T19:00:00Z", "2026-08-20T18:30:00Z", "2026-08-20T20:00:00Z", true)]
    [InlineData("2026-08-20T18:00:00Z", "2026-08-20T19:00:00Z", "2026-08-20T19:00:00Z", "2026-08-20T20:00:00Z", false)]
    public void Overlap_UsesHalfOpenIntervals(
        string leftStart,
        string leftEnd,
        string rightStart,
        string rightEnd,
        bool expected)
    {
        Assert.Equal(expected, PlaytimeTimelineRules.Overlaps(
            DateTimeOffset.Parse(leftStart),
            DateTimeOffset.Parse(leftEnd),
            DateTimeOffset.Parse(rightStart),
            DateTimeOffset.Parse(rightEnd)));
    }

    private static HistoricalEvidence Evidence(
        EvidenceKind kind,
        DateTimeOffset start,
        DateTimeOffset end,
        TimeSpan duration)
    {
        return new HistoricalEvidence(
            Guid.NewGuid(),
            GameId,
            HistoricalSource.Srum,
            kind,
            PlaytimeMetric.Foreground,
            Confidence.Estimated,
            start,
            end,
            duration);
    }
}
