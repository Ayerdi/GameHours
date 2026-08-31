using GameHours.Core.Domain;
using GameHours.Core.Timeline;
using Xunit;

namespace GameHours.Tests;

public sealed class SrumBaselineEvidenceFactoryTests
{
    [Fact]
    public void Create_builds_estimated_pre_cutover_baseline()
    {
        var gameId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var first = DateTimeOffset.Parse("2026-08-06T17:00:00Z");
        var last = DateTimeOffset.Parse("2026-08-20T17:00:00Z");
        var cutover = DateTimeOffset.Parse("2026-08-20T21:32:33.9529435Z");
        var duration = TimeSpan.FromHours(53.766);

        var evidence = SrumBaselineEvidenceFactory.Create(
            gameId,
            first,
            last,
            duration,
            cutover);

        Assert.Equal(gameId, evidence.GameId);
        Assert.Equal(HistoricalSource.Srum, evidence.Source);
        Assert.Equal(EvidenceKind.Baseline, evidence.Kind);
        Assert.Equal(PlaytimeMetric.Foreground, evidence.Metric);
        Assert.Equal(Confidence.Estimated, evidence.Confidence);
        Assert.Equal(first, evidence.PeriodStartUtc);
        Assert.Equal(last, evidence.PeriodEndUtc);
        Assert.Equal(duration, evidence.Duration);
        Assert.True(evidence.PeriodEndUtc <= cutover);
    }

    [Fact]
    public void Create_is_idempotent_for_same_game_and_cutover()
    {
        var gameId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var cutover = DateTimeOffset.Parse("2026-08-20T21:32:33Z");

        var first = SrumBaselineEvidenceFactory.Create(
            gameId,
            DateTimeOffset.Parse("2026-08-20T14:58:00Z"),
            DateTimeOffset.Parse("2026-08-20T19:02:00Z"),
            TimeSpan.FromHours(4.067),
            cutover);

        var second = SrumBaselineEvidenceFactory.Create(
            gameId,
            DateTimeOffset.Parse("2026-08-20T14:00:00Z"),
            DateTimeOffset.Parse("2026-08-20T19:10:00Z"),
            TimeSpan.FromHours(4.2),
            cutover);

        Assert.Equal(first.Id, second.Id);
    }

    [Fact]
    public void Create_expands_coverage_backwards_when_sample_span_is_shorter_than_facetime()
    {
        var cutover = DateTimeOffset.Parse("2026-08-20T21:32:33Z");
        var firstObserved = DateTimeOffset.Parse("2026-08-20T19:00:00Z");
        var lastObserved = DateTimeOffset.Parse("2026-08-20T19:30:00Z");
        var duration = TimeSpan.FromHours(1);

        var evidence = SrumBaselineEvidenceFactory.Create(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            firstObserved,
            lastObserved,
            duration,
            cutover);

        Assert.Equal(DateTimeOffset.Parse("2026-08-20T18:30:00Z"), evidence.PeriodStartUtc);
        Assert.Equal(lastObserved, evidence.PeriodEndUtc);
        Assert.Equal(duration, evidence.Duration);
    }
}
