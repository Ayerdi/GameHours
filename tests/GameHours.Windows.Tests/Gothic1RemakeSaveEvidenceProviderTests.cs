using GameHours.Core.Domain;
using GameHours.Windows.Achievements.Evidence;

namespace GameHours.Windows.Tests;

public sealed class Gothic1RemakeSaveEvidenceProviderTests : IDisposable
{
    private readonly string _saveDirectory = Path.Combine(Path.GetTempPath(), $"GameHours-Gothic-{Guid.NewGuid():N}");

    [Fact]
    public async Task ReadAsync_OnlyAppliesToGothicSteamAppId()
    {
        var parser = new FakeParser();
        var provider = CreateProvider(parser, ProvingRule());

        var result = await provider.ReadAsync(Request(platformAppId: "999999"));

        Assert.Equal(AchievementEvidenceReadStatus.NotApplicable, result.Status);
        Assert.Equal(0, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_WithoutSavesReturnsNoEvidence()
    {
        var provider = CreateProvider(new FakeParser(), ProvingRule());

        var result = await provider.ReadAsync(Request());

        Assert.Equal(AchievementEvidenceReadStatus.NoEvidence, result.Status);
        Assert.Empty(result.Evidence);
    }

    [Fact]
    public async Task ReadAsync_EmitsAuditablePositiveProofFromInjectedParserAndRule()
    {
        var save = CreateSave("slot1.sav", "state");
        var provider = CreateProvider(new FakeParser(), ProvingRule(version: 7));

        var result = await provider.ReadAsync(Request());

        var proof = Assert.Single(result.Evidence);
        Assert.Equal(AchievementEvidenceReadStatus.Success, result.Status);
        Assert.Equal(Gothic1RemakeSaveEvidenceProvider.ProviderName, proof.Provider);
        Assert.Equal("gothic.quest.chapter-one", proof.RuleId);
        Assert.Equal(7, proof.RuleVersion);
        Assert.Equal(save, proof.SourcePath);
        Assert.Equal("meta:v1:5:639237744000000000", proof.SourceFingerprint);
    }

    [Fact]
    public async Task ReadAsync_UnchangedMetadataDoesNotParseAgain()
    {
        CreateSave("slot1.sav", "state");
        var parser = new FakeParser();
        var provider = CreateProvider(parser, ProvingRule());

        await provider.ReadAsync(Request());
        await provider.ReadAsync(Request());

        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_ChangedMetadataParsesAgain()
    {
        var save = CreateSave("slot1.sav", "state");
        var parser = new FakeParser();
        var provider = CreateProvider(parser, ProvingRule());

        await provider.ReadAsync(Request());
        File.WriteAllText(save, "changed-state");
        File.SetLastWriteTimeUtc(save, new DateTime(2026, 8, 31, 12, 1, 0, DateTimeKind.Utc));
        await provider.ReadAsync(Request());

        Assert.Equal(2, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_ConcurrentReadsShareCachedParse()
    {
        CreateSave("slot1.sav", "state");
        var parserStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseParser = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parser = new FakeParser(async (_, cancellationToken) =>
        {
            parserStarted.TrySetResult();
            await releaseParser.Task.WaitAsync(cancellationToken);
            return State();
        });
        var provider = CreateProvider(parser, ProvingRule());

        var firstRead = provider.ReadAsync(Request());
        await parserStarted.Task;
        var secondRead = provider.ReadAsync(Request());
        releaseParser.SetResult();

        await Task.WhenAll(firstRead, secondRead);
        Assert.Equal(1, parser.ParseCount);
    }

    [Fact]
    public async Task ReadAsync_SaveChangedDuringParseDoesNotEmitEvidence()
    {
        var save = CreateSave("slot1.sav", "state");
        var parser = new FakeParser((_, _) =>
        {
            File.AppendAllText(save, "-changed");
            File.SetLastWriteTimeUtc(save, new DateTime(2026, 8, 31, 12, 1, 0, DateTimeKind.Utc));
            return Task.FromResult(State());
        });
        var provider = CreateProvider(parser, ProvingRule());

        var result = await provider.ReadAsync(Request());

        Assert.Equal(AchievementEvidenceReadStatus.Failed, result.Status);
        Assert.Empty(result.Evidence);
        Assert.Contains("changed while it was being inspected", Assert.Single(result.Diagnostics).Detail);
    }

    [Fact]
    public async Task ReadAsync_WaitingReadUsesMetadataObservedInsideCacheGate()
    {
        var save = CreateSave("slot1.sav", "state");
        var secondParseStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecondParse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invocation = 0;
        var parser = new FakeParser(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref invocation) == 2)
            {
                secondParseStarted.SetResult();
                await releaseSecondParse.Task.WaitAsync(cancellationToken);
            }

            return State();
        });
        var provider = CreateProvider(parser, ProvingRule());
        await provider.ReadAsync(Request());

        File.AppendAllText(save, "-v2");
        File.SetLastWriteTimeUtc(save, new DateTime(2026, 8, 31, 12, 1, 0, DateTimeKind.Utc));
        var changingRead = provider.ReadAsync(Request());
        await secondParseStarted.Task;
        var waitingRead = provider.ReadAsync(Request());
        File.AppendAllText(save, "-v3");
        File.SetLastWriteTimeUtc(save, new DateTime(2026, 8, 31, 12, 2, 0, DateTimeKind.Utc));
        releaseSecondParse.SetResult();

        Assert.Equal(AchievementEvidenceReadStatus.Failed, (await changingRead).Status);
        var stableResult = await waitingRead;
        Assert.Equal(AchievementEvidenceReadStatus.Success, stableResult.Status);
        Assert.Equal(3, parser.ParseCount);
        Assert.StartsWith(
            "meta:v1:11:",
            Assert.Single(stableResult.Evidence).SourceFingerprint ?? string.Empty);
    }

    [Fact]
    public async Task ReadAsync_FailedSaveDoesNotEraseProofFromAnotherSave()
    {
        var healthy = CreateSave("healthy.sav", "healthy");
        CreateSave("broken.sav", "broken");
        var parser = new FakeParser(path => path.EndsWith("broken.sav", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidDataException("Unsupported save payload.")
            : State());
        var provider = CreateProvider(parser, ProvingRule());

        var result = await provider.ReadAsync(Request());

        Assert.Equal(AchievementEvidenceReadStatus.Success, result.Status);
        Assert.Equal(healthy, Assert.Single(result.Evidence).SourcePath);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("Unsupported save payload.", diagnostic.Detail);
        Assert.EndsWith("broken.sav", diagnostic.SourcePath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProviderChain_PreservesDiagnosticFromPartiallySuccessfulRead()
    {
        CreateSave("healthy.sav", "healthy");
        CreateSave("broken.sav", "broken");
        var parser = new FakeParser(path => path.EndsWith("broken.sav", StringComparison.OrdinalIgnoreCase)
            ? throw new InvalidDataException("Unsupported save payload.")
            : State());
        var chain = new AchievementEvidenceProviderChain([CreateProvider(parser, ProvingRule())]);

        var result = await chain.ReadAsync(Request());

        Assert.Single(result.Evidence);
        Assert.Equal("Unsupported save payload.", Assert.Single(result.Diagnostics).Detail);
    }

    [Fact]
    public async Task ReadAsync_HonorsCancellationBeforeEnumeration()
    {
        CreateSave("slot1.sav", "state");
        var provider = CreateProvider(new FakeParser(), ProvingRule());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ReadAsync(Request(), cancellation.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_saveDirectory))
        {
            Directory.Delete(_saveDirectory, recursive: true);
        }
    }

    private Gothic1RemakeSaveEvidenceProvider CreateProvider(
        FakeParser parser,
        params IAchievementEvidenceRule<Gothic1RemakeSaveState>[] rules) =>
        new(parser, rules, new Gothic1RemakeSaveDirectoryLocator(_saveDirectory));

    private string CreateSave(string name, string contents)
    {
        Directory.CreateDirectory(_saveDirectory);
        var path = Path.Combine(_saveDirectory, name);
        File.WriteAllText(path, contents);
        File.SetLastWriteTimeUtc(path, new DateTime(2026, 8, 31, 12, 0, 0, DateTimeKind.Utc));
        return path;
    }

    private static AchievementEvidenceRequest Request(string platformAppId = Gothic1RemakeSaveEvidenceProvider.SteamAppId) =>
        new(Guid.NewGuid(), "Gothic 1 Remake", null, platformAppId, DateTimeOffset.Parse("2026-08-31T12:30:00Z"));

    private static Gothic1RemakeSaveState State() => new(
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "chapter-one" },
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(StringComparer.OrdinalIgnoreCase));

    private static IAchievementEvidenceRule<Gothic1RemakeSaveState> ProvingRule(int version = 1) =>
        new FakeRule(version);

    private sealed class FakeParser : IGothic1RemakeSaveParser
    {
        private readonly Func<string, CancellationToken, Task<Gothic1RemakeSaveState>> _parse;

        public FakeParser(Func<string, Gothic1RemakeSaveState>? parse = null) =>
            _parse = (path, _) => Task.FromResult(parse?.Invoke(path) ?? State());

        public FakeParser(Func<string, CancellationToken, Task<Gothic1RemakeSaveState>> parse) =>
            _parse = parse;

        public int ParseCount { get; private set; }

        public async Task<Gothic1RemakeSaveState> ParseAsync(string savePath, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ParseCount++;
            return await _parse(savePath, cancellationToken);
        }
    }

    private sealed class FakeRule : IAchievementEvidenceRule<Gothic1RemakeSaveState>
    {
        public FakeRule(int version) => Version = version;

        public string AchievementApiName => "GOTHIC_CHAPTER_ONE";
        public string RuleId => "gothic.quest.chapter-one";
        public int Version { get; }

        public bool TryProve(Gothic1RemakeSaveState state, out string detail)
        {
            var proven = state.CompletedQuests.Contains("chapter-one");
            detail = proven ? "Chapter one quest state proved from save." : string.Empty;
            return proven;
        }
    }
}
