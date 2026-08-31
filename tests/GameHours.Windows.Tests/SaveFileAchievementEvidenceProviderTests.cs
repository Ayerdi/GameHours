using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class SaveFileAchievementEvidenceProviderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"GameHours-Saves-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadAsync_ReusesProviderForDistinctProfiles()
    {
        CreateSave("alpha.one", "alpha");
        CreateSave("beta.two", "beta");
        var alpha = CreateProvider("alpha", "A", "*.one", new FakeParser(path => Path.GetFileName(path)), new ProvingRule("ALPHA", "alpha.rule"));
        var beta = CreateProvider("beta", "B", "*.two", new FakeParser(path => Path.GetFileName(path)), new ProvingRule("BETA", "beta.rule"));

        var alphaResult = await alpha.ReadAsync(Request("A"));
        var betaResult = await beta.ReadAsync(Request("B"));

        Assert.Equal("ALPHA", Assert.Single(alphaResult.Evidence).ApiName);
        Assert.Equal("BETA", Assert.Single(betaResult.Evidence).ApiName);
        Assert.Equal(AchievementEvidenceReadStatus.NotApplicable, (await alpha.ReadAsync(Request("B"))).Status);
    }

    [Fact]
    public async Task ReadAsync_ConcurrentReadersOfOneFileShareOneParse()
    {
        CreateSave("slot.sav", "state");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new FakeParser(async (_, _) => { started.TrySetResult(); await release.Task; return "proof"; });
        var provider = CreateProvider("shared", "A", "*.sav", parser, new ProvingRule("ONE", "one"));

        var first = provider.ReadAsync(Request("A"));
        await started.Task;
        var second = provider.ReadAsync(Request("A"));
        release.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_IndependentFilesParseWithoutGlobalSerialization()
    {
        CreateSave("one.sav", "one");
        CreateSave("two.sav", "two");
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        var parser = new FakeParser(async (_, _) =>
        {
            if (Interlocked.Increment(ref started) == 2) bothStarted.TrySetResult();
            await release.Task;
            return "proof";
        });
        var provider = CreateProvider("parallel", "A", "*.sav", parser, new ProvingRule("ONE", "one"));

        var read = provider.ReadAsync(Request("A"));
        await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        release.SetResult();
        await read;

        Assert.Equal(2, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_InvalidatesCacheAndRejectsAFileChangedDuringParsing()
    {
        var path = CreateSave("slot.sav", "one");
        var parser = new FakeParser();
        var provider = CreateProvider("cache", "A", "*.sav", parser, new ProvingRule("ONE", "one"));
        await provider.ReadAsync(Request("A"));
        File.WriteAllText(path, "two-two");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 13, 0, 0, DateTimeKind.Utc));
        await provider.ReadAsync(Request("A")).WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(2, parser.ParseCount);

        var changingParser = new FakeParser((_, _) =>
        {
            File.AppendAllText(path, "-changed");
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc));
            return Task.FromResult("proof");
        });
        var changingProvider = CreateProvider("changing", "A", "*.sav", changingParser, new ProvingRule("ONE", "one"));
        var result = await changingProvider.ReadAsync(Request("A"));

        Assert.Equal(AchievementEvidenceReadStatus.Failed, result.Status);
        Assert.Contains("changed while it was being inspected", Assert.Single(result.Diagnostics).Detail);
    }

    [Fact]
    public async Task ReadAsync_WaitingReaderObservesMetadataAfterEnteringFileGate()
    {
        var path = CreateSave("slot.sav", "one");
        var parseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var parser = new FakeParser(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref invocation) == 2)
            {
                parseStarted.SetResult();
                await releaseParse.Task.WaitAsync(cancellationToken);
            }

            return "proof";
        });
        var provider = CreateProvider("stable", "A", "*.sav", parser, new ProvingRule("ONE", "one"));
        await provider.ReadAsync(Request("A"));

        File.AppendAllText(path, "-v2");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 13, 0, 0, DateTimeKind.Utc));
        var changingRead = provider.ReadAsync(Request("A"));
        await parseStarted.Task;
        File.AppendAllText(path, "-v3");
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 14, 0, 0, DateTimeKind.Utc));
        var waitingRead = provider.ReadAsync(Request("A"));
        Assert.Equal(2, parser.ParseCount);
        releaseParse.SetResult();

        Assert.Equal(AchievementEvidenceReadStatus.Failed, (await changingRead).Status);
        var stableResult = await waitingRead;
        Assert.Equal(AchievementEvidenceReadStatus.Success, stableResult.Status);
        Assert.Equal(3, parser.ParseCount);
        Assert.Matches(
            "^meta:v2:[0-9A-F]{64}:9:[0-9]+$",
            Assert.Single(stableResult.Evidence).SourceFingerprint ?? string.Empty);
    }

    [Fact]
    public async Task ReadAsync_DoesNotHideUnexpectedParserDefects()
    {
        CreateSave("slot.sav", "state");
        var parser = new FakeParser((_, _) => throw new InvalidOperationException("Parser defect."));
        var provider = CreateProvider("defect", "A", "*.sav", parser, new ProvingRule("ONE", "one"));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.ReadAsync(Request("A")));

        Assert.Equal("Parser defect.", exception.Message);
    }

    [Fact]
    public async Task ReadAsync_DoesNotHideRuleDefectsThatResembleFileFailures()
    {
        CreateSave("slot.sav", "state");
        var provider = CreateProvider(
            "rule-defect",
            "A",
            "*.sav",
            new FakeParser(),
            new ThrowingRule());

        await Assert.ThrowsAsync<IOException>(() => provider.ReadAsync(Request("A")));
    }

    [Fact]
    public async Task ReadAsync_CancelledWaiterDoesNotCancelSharedParse()
    {
        CreateSave("slot.sav", "state");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new FakeParser(async (_, _) =>
        {
            started.SetResult();
            await release.Task;
            return "proof";
        });
        var provider = CreateProvider("cancel", "A", "*.sav", parser, new ProvingRule("ONE", "one"));
        using var cancellation = new CancellationTokenSource();

        var cancelledRead = provider.ReadAsync(Request("A"), cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRead);
        release.SetResult();

        var completedRead = await provider.ReadAsync(Request("A"));
        Assert.Equal(AchievementEvidenceReadStatus.Success, completedRead.Status);
        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task Dispose_CancelsSharedParserWork()
    {
        CreateSave("slot.sav", "state");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new FakeParser(async (_, cancellationToken) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return "proof";
        });
        var provider = CreateProvider("lifetime", "A", "*.sav", parser, new ProvingRule("ONE", "one"));
        var read = provider.ReadAsync(Request("A"));
        await started.Task;

        provider.Dispose();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Fact]
    public async Task ReadAsync_ReportsLocatorAccessFailure()
    {
        var provider = new SaveFileAchievementEvidenceProvider<string>(
            "locator",
            _ => true,
            () => throw new UnauthorizedAccessException("Denied."),
            new FakeParser(),
            [new ProvingRule("ONE", "one")]);

        var result = await provider.ReadAsync(Request("A"));

        Assert.Equal(AchievementEvidenceReadStatus.Failed, result.Status);
        Assert.Equal("Denied.", Assert.Single(result.Diagnostics).Detail);
    }

    [Fact]
    public async Task ReadAsync_PrunesCacheForFilesNoLongerDiscovered()
    {
        var path = CreateSave("slot.sav", "same");
        var parser = new FakeParser();
        var provider = CreateProvider("prune", "A", "*.sav", parser, new ProvingRule("ONE", "one"));
        await provider.ReadAsync(Request("A"));

        File.Delete(path);
        Assert.Equal(AchievementEvidenceReadStatus.NoEvidence, (await provider.ReadAsync(Request("A"))).Status);
        CreateSave("slot.sav", "same");
        await provider.ReadAsync(Request("A"));

        Assert.Equal(2, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_EmitsOnlyPositiveAuditableProofs()
    {
        var save = CreateSave("slot.sav", "state");
        var provider = CreateProvider("audit", "A", "*.sav", new FakeParser(), new ProvingRule("PROVEN", "proven"), new ProvingRule("NOT_PROVEN", "negative", proves: false));

        var result = await provider.ReadAsync(Request("A"));

        var proof = Assert.Single(result.Evidence);
        Assert.Equal("PROVEN", proof.ApiName);
        Assert.Equal(save, proof.SourcePath);
        Assert.Matches("^meta:v2:[0-9A-F]{64}:5:[0-9]+$", proof.SourceFingerprint ?? string.Empty);
    }

    [Fact]
    public async Task ProviderChain_DoesNotCollapseDistinctPathsWithIdenticalMetadata()
    {
        CreateSave("one.sav", "same");
        CreateSave("two.sav", "same");
        var provider = CreateProvider(
            "identity",
            "A",
            "*.sav",
            new FakeParser(),
            new ProvingRule("PROVEN", "proven"));
        var chain = new AchievementEvidenceProviderChain([provider]);

        var result = await chain.ReadAsync(Request("A"));

        Assert.Equal(2, result.Evidence.Count);
        Assert.Equal(2, result.Evidence.Select(item => item.SourceFingerprint).Distinct().Count());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private SaveFileAchievementEvidenceProvider<string> CreateProvider(
        string name,
        string appId,
        string pattern,
        FakeParser parser,
        params IAchievementEvidenceRule<string>[] rules) =>
        new(
            name,
            request => request.PlatformAppId == appId,
            () => Directory.EnumerateFiles(_directory, pattern, SearchOption.TopDirectoryOnly),
            parser,
            rules);

    private string CreateSave(string name, string contents)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static AchievementEvidenceRequest Request(string appId) => new(Guid.NewGuid(), "Test", null, appId, DateTimeOffset.Parse("2026-08-31T12:30:00Z"));

    private sealed class FakeParser : ISaveStateParser<string>
    {
        private readonly Func<string, CancellationToken, Task<string>> _parse;
        public FakeParser(Func<string, CancellationToken, Task<string>>? parse = null) => _parse = parse ?? ((_, _) => Task.FromResult("proof"));
        public FakeParser(Func<string, string> parse) : this((path, _) => Task.FromResult(parse(path))) { }
        private int _parseCount;
        public int ParseCount => _parseCount;
        public async Task<string> ParseAsync(string savePath, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _parseCount);
            return await _parse(savePath, cancellationToken);
        }
    }

    private sealed class ProvingRule : IAchievementEvidenceRule<string>
    {
        private readonly bool _proves;
        public ProvingRule(string apiName, string ruleId, bool proves = true) { AchievementApiName = apiName; RuleId = ruleId; _proves = proves; }
        public string AchievementApiName { get; }
        public string RuleId { get; }
        public int Version => 1;
        public bool TryProve(string state, out string detail) { detail = _proves ? "Positive save proof." : string.Empty; return _proves; }
    }

    private sealed class ThrowingRule : IAchievementEvidenceRule<string>
    {
        public string AchievementApiName => "BROKEN";
        public string RuleId => "broken";
        public int Version => 1;

        public bool TryProve(string state, out string detail)
        {
            detail = string.Empty;
            throw new IOException("Rule defect.");
        }
    }
}
