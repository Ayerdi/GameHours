using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class AchievementEvidenceRuleTests
{
    [Fact]
    public void Evaluate_EmitsOnlyRulesThatProveTheirCondition()
    {
        var gameId = Guid.NewGuid();
        var state = new TestState(QuestSucceeded: true, HasRequiredSkill: false);
        var rules = new IAchievementEvidenceRule<TestState>[]
        {
            new TestRule("ACH_QUEST", "quest.succeeded", 1, value => value.QuestSucceeded),
            new TestRule("ACH_SKILL", "skill.learned", 3, value => value.HasRequiredSkill)
        };

        var evidence = AchievementEvidenceRuleEvaluator.Evaluate(
            gameId,
            AchievementEvidenceOrigin.SaveGame,
            "test-save",
            state,
            rules,
            @"C:\Saves\slot.sav",
            "sha256:abc",
            DateTimeOffset.Parse("2026-08-31T00:50:00Z"));

        var proof = Assert.Single(evidence);
        Assert.Equal("ACH_QUEST", proof.ApiName);
        Assert.Equal("quest.succeeded", proof.RuleId);
        Assert.Equal(1, proof.RuleVersion);
        Assert.Contains("proved", proof.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_FalseRuleMeansUnknownAndProducesNoNegativeRecord()
    {
        var rules = new IAchievementEvidenceRule<TestState>[]
        {
            new TestRule("ACH_SKILL", "skill.learned", 1, _ => false)
        };

        var evidence = AchievementEvidenceRuleEvaluator.Evaluate(
            Guid.NewGuid(),
            AchievementEvidenceOrigin.SaveGame,
            "test-save",
            new TestState(false, false),
            rules,
            null,
            null,
            DateTimeOffset.UtcNow);

        Assert.Empty(evidence);
    }

    private sealed record TestState(bool QuestSucceeded, bool HasRequiredSkill);

    private sealed class TestRule : IAchievementEvidenceRule<TestState>
    {
        private readonly Func<TestState, bool> _predicate;

        public TestRule(
            string achievementApiName,
            string ruleId,
            int version,
            Func<TestState, bool> predicate)
        {
            AchievementApiName = achievementApiName;
            RuleId = ruleId;
            Version = version;
            _predicate = predicate;
        }

        public string AchievementApiName { get; }
        public string RuleId { get; }
        public int Version { get; }

        public bool TryProve(TestState state, out string detail)
        {
            if (_predicate(state))
            {
                detail = $"{RuleId} proved from persisted state.";
                return true;
            }

            detail = string.Empty;
            return false;
        }
    }
}
