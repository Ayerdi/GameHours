using GameHours.Core.Abstractions;
using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Tests;

public sealed class ExplicitExecutableRoleResolverTests
{
    [Theory]
    [InlineData(ExecutableRole.Ignored)]
    [InlineData(ExecutableRole.Launcher)]
    [InlineData(ExecutableRole.Helper)]
    [InlineData(ExecutableRole.AntiCheat)]
    [InlineData(ExecutableRole.Updater)]
    [InlineData(ExecutableRole.CrashHandler)]
    public async Task HelperLikeUserOverrideShortCircuitsInnerResolver(ExecutableRole role)
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "override.exe");
        var overrides = new FakeOverrideStore(path, role);
        var resolver = new ExplicitExecutableRoleResolver(new FailIfCalledResolver(), overrides, new RecordingHistory());

        var resolution = await resolver.ResolveAsync(new ProcessSnapshot(42, "override", path, null));

        Assert.Null(resolution.Game);
        Assert.Equal(0, resolution.Confidence);
        Assert.Equal("user_role_override", resolution.Method);
        Assert.True(resolution.IsHelperProcess);
        Assert.Equal(role, resolution.Role);
    }

    [Fact]
    public async Task OverrideShortCircuitStillRecordsProcessIdentityForChildRecovery()
    {
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "launcher.exe");
        var observedAt = new DateTimeOffset(2026, 8, 27, 18, 0, 0, TimeSpan.Zero);
        var history = new RecordingHistory();
        var resolver = new ExplicitExecutableRoleResolver(
            new FailIfCalledResolver(),
            new FakeOverrideStore(path, ExecutableRole.Launcher),
            history,
            () => observedAt);
        var process = new ProcessSnapshot(44, "launcher", path, observedAt.AddMinutes(-1), ParentProcessId: 7);

        await resolver.ResolveAsync(process);

        var recorded = Assert.Single(history.Observed);
        Assert.Equal(process.ProcessId, recorded.Process.ProcessId);
        Assert.Equal(Path.GetFullPath(path), recorded.Process.ExecutablePath);
        Assert.Equal(observedAt, recorded.ObservedAtUtc);
    }

    [Fact]
    public async Task MissingOverrideDelegatesWithoutChangingResolution()
    {
        var expected = new GameResolution(null, 0.65, "candidate", false, ExecutableRole.Unknown);
        var inner = new CountingResolver(expected);
        var resolver = new ExplicitExecutableRoleResolver(inner, new FakeOverrideStore(null, ExecutableRole.Unknown), new RecordingHistory());
        var path = Path.Combine(Path.GetTempPath(), "GameHoursTests", "candidate.exe");

        var actual = await resolver.ResolveAsync(new ProcessSnapshot(43, "candidate", path, null));

        Assert.Equal(expected, actual);
        Assert.Equal(1, inner.CallCount);
    }

    private sealed class FakeOverrideStore(string? path, ExecutableRole role) : IExecutableRoleOverrideStore
    {
        private readonly string? _path = path is null ? null : Path.GetFullPath(path);

        public bool TryGetRole(string executablePath, out ExecutableRole resolvedRole)
        {
            if (_path is not null && string.Equals(_path, Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase))
            {
                resolvedRole = role;
                return true;
            }

            resolvedRole = ExecutableRole.Unknown;
            return false;
        }

        public void SetRole(string executablePath, ExecutableRole role) => throw new NotSupportedException();
        public void Remove(string executablePath) => throw new NotSupportedException();
    }

    private sealed class RecordingHistory : IRecentProcessIdentityHistory
    {
        public List<(ProcessSnapshot Process, DateTimeOffset ObservedAtUtc)> Observed { get; } = new();

        public void Observe(ProcessSnapshot process, DateTimeOffset observedAtUtc) =>
            Observed.Add((process, observedAtUtc));

        public string? TryGetExecutablePath(int processId, DateTimeOffset observedAtUtc, DateTimeOffset? childStartedAtUtc = null) => null;
        public int? TryGetParentProcessId(int processId, DateTimeOffset observedAtUtc) => null;
        public DateTimeOffset? TryGetStartedAtUtc(int processId, DateTimeOffset observedAtUtc) => null;
    }

    private sealed class FailIfCalledResolver : IGameResolver
    {
        public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Explicit helper-like override must win before the inner resolver.");
    }

    private sealed class CountingResolver(GameResolution resolution) : IGameResolver
    {
        public int CallCount { get; private set; }

        public Task<GameResolution> ResolveAsync(ProcessSnapshot process, CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(resolution);
        }
    }
}
