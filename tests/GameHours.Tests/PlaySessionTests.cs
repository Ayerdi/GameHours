using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class PlaySessionTests
{
    [Fact]
    public void GothicProbeSample_HasExpectedDuration()
    {
        var session = new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.Parse("2026-08-20T16:10:21.953603Z"),
            DateTimeOffset.Parse("2026-08-20T16:11:27.133611Z"),
            CaptureMethod.Reconciliation,
            Confidence.High,
            "ProcessReconciliation");

        Assert.Equal(65_180, Math.Round(session.Duration.TotalMilliseconds));
    }

    [Fact]
    public void Session_RejectsEstimatedConfidence()
    {
        Assert.Throws<ArgumentException>(() => new PlaySession(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(1),
            CaptureMethod.Reconciliation,
            Confidence.Estimated));
    }
}
