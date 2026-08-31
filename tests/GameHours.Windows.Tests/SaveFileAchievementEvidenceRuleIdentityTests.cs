using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class SaveFileAchievementEvidenceRuleIdentityTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"GameHours-RuleIdentity-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadAsync_NoEvidenceStillReportsCurrentRuleRevision()
    {
        var provider = CreateProvider();

        var result = await provider.ReadAsync(Request("123"));

        Assert.Equal(AchievementEvidenceReadStatus.NoEvidence, result.Status);
        Assert.Equal(
            new AchievementEvidenceRuleIdentity(
                "generic-save",
                "ACH_STORY",
                "story.completed",
                2),
            Assert.Single(result.ActiveRuleIdentities));
    }

    [Fact]
    public async Task ReadAsync_NotApplicableDoesNotActivateRulesForAnotherGame()
    {
        var provider = CreateProvider();

        var result = await provider.ReadAsync(Request("999"));

        Assert.Equal(AchievementEvidenceReadStatus.NotApplicable, result.Status);
        Assert.Empty(result.ActiveRuleIdentities);
    }

    [Fact]
    public async Task ReadAsync_EnvironmentalFailureStillReportsCurrentRuleRevision()
    {
        var provider = new SaveFileAchievementEvidenceProvider<string>(
            "generic-save",
            request => request.PlatformAppId == "123",
            () => throw new UnauthorizedAccessException("Denied."),
            new Parser(),
            new[] { new Rule() });

        var result = await provider.ReadAsync(Request("123"));

        Assert.Equal(AchievementEvidenceReadStatus.Failed, result.Status);
        Assert.Equal(2, Assert.Single(result.ActiveRuleIdentities).RuleVersion);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private SaveFileAchievementEvidenceProvider<string> CreateProvider() =>
        new(
            "generic-save",
            request => request.PlatformAppId == "123",
            () => Directory.EnumerateFiles(_directory, "*.sav", SearchOption.TopDirectoryOnly),
            new Parser(),
            new[] { new Rule() });

    private static AchievementEvidenceRequest Request(string appId) =>
        new(
            Guid.NewGuid(),
            "Generic Game",
            null,
            appId,
            DateTimeOffset.Parse("2026-08-31T10:20:00Z"));

    private sealed class Parser : ISaveStateParser<string>
    {
        public Task<string> ParseAsync(
            string savePath,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("state");
    }

    private sealed class Rule : IAchievementEvidenceRule<string>
    {
        public string AchievementApiName => "ACH_STORY";
        public string RuleId => "story.completed";
        public int Version => 2;

        public bool TryProve(string state, out string detail)
        {
            detail = string.Empty;
            return false;
        }
    }
}
