using GameHours.Core.Discovery;
using GameHours.Core.Monitoring;
using GameHours.Windows.Discovery;

namespace GameHours.Windows.Tests;

public sealed class ExecutableRoleOverrideStoreTests
{
    [Fact]
    public void UserOverrideWinsOverAutomaticExecutableRole()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var overridesPath = Path.Combine(root, "roles.json");
        var executablePath = Path.Combine(root, "Game-Win64-Shipping.exe");
        var overrides = new LocalExecutableRoleOverrideStore(overridesPath);
        overrides.SetRole(executablePath, ExecutableRole.Ignored);
        var collector = new WindowsProcessEvidenceCollector(
            new EmptyGameConfigStore(),
            inspectLiveProcess: false,
            roleOverrides: new LocalExecutableRoleOverrideStore(overridesPath));

        var evidence = collector.Collect(new ProcessSnapshot(0, "game", executablePath, null));

        Assert.Equal(ExecutableRole.Ignored, evidence.Role);
        Assert.Contains(
            evidence.Evidence,
            item => item.Kind == GameDetectionEvidenceKind.ExecutableRole
                    && item.Detail.Contains("User role override", StringComparison.Ordinal));

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void StoreReloadsChangesWrittenByAnotherInstance()
    {
        var root = Path.Combine(Path.GetTempPath(), "GameHoursTests", Guid.NewGuid().ToString("N"));
        var overridesPath = Path.Combine(root, "roles.json");
        var executablePath = Path.Combine(root, "launcher.exe");
        var reader = new LocalExecutableRoleOverrideStore(overridesPath);
        var writer = new LocalExecutableRoleOverrideStore(overridesPath);

        Assert.False(reader.TryGetRole(executablePath, out _));
        writer.SetRole(executablePath, ExecutableRole.Launcher);

        Assert.True(reader.TryGetRole(executablePath, out var role));
        Assert.Equal(ExecutableRole.Launcher, role);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch
        {
        }
    }

    private sealed class EmptyGameConfigStore : IWindowsGameConfigStore
    {
        public bool ContainsExecutable(string executablePath) => false;
    }
}
