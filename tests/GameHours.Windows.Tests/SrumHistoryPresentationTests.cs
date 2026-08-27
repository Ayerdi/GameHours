using GameHours.Core.Domain;
using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class SrumHistoryPresentationTests
{
    [Fact]
    public void BaselineIsSelectedByDefaultButGapRecoveryIsOptIn()
    {
        var baseline = new SrumHistoryWindow.SrumCandidateViewModel(CreateCandidate(EvidenceKind.Baseline));
        var gapRecovery = new SrumHistoryWindow.SrumCandidateViewModel(CreateCandidate(EvidenceKind.GapRecovery));

        Assert.True(baseline.IsSelected);
        Assert.False(gapRecovery.IsSelected);
        Assert.True(baseline.CanSelect);
        Assert.True(gapRecovery.CanSelect);
    }

    [Theory]
    [InlineData(164.4, "2 h 44 min")]
    [InlineData(225, "3 h 45 min")]
    [InlineData(660, "11 h")]
    [InlineData(44, "44 min")]
    public void PlaytimeUsesHumanReadableHoursAndMinutes(double minutes, string expected)
    {
        var candidate = CreateCandidate(EvidenceKind.GapRecovery, TimeSpan.FromMinutes(minutes));
        var viewModel = new SrumHistoryWindow.SrumCandidateViewModel(candidate);

        Assert.Equal(expected, viewModel.PlaytimeText);
    }

    [Fact]
    public void ImportedCandidateCannotBePreselected()
    {
        var candidate = CreateCandidate(EvidenceKind.Baseline, alreadyImported: true);
        var viewModel = new SrumHistoryWindow.SrumCandidateViewModel(candidate);

        Assert.False(viewModel.IsSelected);
        Assert.False(viewModel.CanSelect);
        Assert.Equal("Importado", viewModel.StateText);
    }

    private static DesktopSrumHistoryCandidate CreateCandidate(
        EvidenceKind kind,
        TimeSpan? playtime = null,
        bool alreadyImported = false)
    {
        var game = new TrackedGame(Guid.NewGuid(), "Example Game");
        var start = new DateTimeOffset(2026, 8, 27, 10, 0, 0, TimeSpan.Zero);
        return new DesktopSrumHistoryCandidate(
            game,
            kind,
            playtime ?? TimeSpan.FromHours(2),
            start,
            start.AddHours(2),
            new[] { @"C:\Games\Example\game.exe" },
            Array.Empty<HistoricalEvidence>(),
            alreadyImported);
    }
}
