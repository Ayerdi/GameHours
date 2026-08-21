using GameHours.Core.Domain;
using GameHours.Core.Timeline;

namespace GameHours.Tests;

public sealed class HistoricalCoverageSummarizerTests
{
    [Fact]
    public void Build_ReturnsNullWhenNoHistoricalEvidenceExists()
    {
        var result = HistoricalCoverageSummarizer.Build(
            Guid.NewGuid(),
            Array.Empty<HistoricalEvidence>());

        Assert.Null(result);
    }

    [Fact]
    public void Build_SummarizesKnownDurationCoverageAndSourceProvenance()
    {
        var gameId = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-08-01T10:00:00Z");
        var evidence = new[]
        {
            Evidence(
                gameId,
                HistoricalSource.Srum,
                PlaytimeMetric.Foreground,
                Confidence.High,
                start,
                start.AddHours(2),
                TimeSpan.FromMinutes(30)),
            Evidence(
                gameId,
                HistoricalSource.Srum,
                PlaytimeMetric.Runtime,
                Confidence.Estimated,
                start.AddDays(1),
                start.AddDays(1).AddHours(3),
                TimeSpan.FromMinutes(45)),
            Evidence(
                gameId,
                HistoricalSource.UserAssist,
                PlaytimeMetric.Foreground,
                Confidence.High,
                start.AddDays(2),
                start.AddDays(2).AddMinutes(20),
                TimeSpan.FromMinutes(5))
        };

        var result = HistoricalCoverageSummarizer.Build(gameId, evidence);

        Assert.NotNull(result);
        Assert.Equal(TimeSpan.FromMinutes(80), result.KnownDuration);
        Assert.Equal(start, result.FirstKnownActivityAtUtc);
        Assert.Equal(start.AddDays(2).AddMinutes(20), result.LastKnownActivityAtUtc);
        Assert.True(result.ContainsEstimatedEvidence);
        Assert.Equal(2, result.Sources.Count);

        var srum = Assert.Single(
            result.Sources,
            source => source.Source == HistoricalSource.Srum);
        Assert.Equal(TimeSpan.FromMinutes(75), srum.KnownDuration);
        Assert.Equal(Confidence.Estimated, srum.MinimumConfidence);
        Assert.Contains(PlaytimeMetric.Foreground, srum.Metrics);
        Assert.Contains(PlaytimeMetric.Runtime, srum.Metrics);

        var userAssist = Assert.Single(
            result.Sources,
            source => source.Source == HistoricalSource.UserAssist);
        Assert.Equal(TimeSpan.FromMinutes(5), userAssist.KnownDuration);
        Assert.Equal(Confidence.High, userAssist.MinimumConfidence);
    }

    [Fact]
    public void Build_RejectsEvidenceFromAnotherGame()
    {
        var requestedGameId = Guid.NewGuid();
        var otherGameId = Guid.NewGuid();
        var start = DateTimeOffset.Parse("2026-08-01T10:00:00Z");

        var exception = Assert.Throws<ArgumentException>(() =>
            HistoricalCoverageSummarizer.Build(
                requestedGameId,
                new[]
                {
                    Evidence(
                        otherGameId,
                        HistoricalSource.Srum,
                        PlaytimeMetric.Foreground,
                        Confidence.High,
                        start,
                        start.AddHours(1),
                        TimeSpan.FromMinutes(10))
                }));

        Assert.Equal("evidence", exception.ParamName);
    }

    private static HistoricalEvidence Evidence(
        Guid gameId,
        HistoricalSource source,
        PlaytimeMetric metric,
        Confidence confidence,
        DateTimeOffset periodStartUtc,
        DateTimeOffset periodEndUtc,
        TimeSpan duration) =>
        new(
            Guid.NewGuid(),
            gameId,
            source,
            EvidenceKind.Baseline,
            metric,
            confidence,
            periodStartUtc,
            periodEndUtc,
            duration);
}
