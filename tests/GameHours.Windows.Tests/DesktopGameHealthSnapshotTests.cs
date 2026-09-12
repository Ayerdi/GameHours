using GameHours.Desktop;

namespace GameHours.Windows.Tests;

public sealed class DesktopGameHealthSnapshotTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Build_ReadyGame_StaysHealthyWithoutOptionalHistoryOrAchievements()
    {
        var executable = CreateExecutable();
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: executable),
            isTracking: true,
            isActive: false,
            DateTimeOffset.Parse("2026-09-12T10:00:00Z"));

        Assert.Equal(DesktopGameHealthState.Ready, snapshot.OverallState);
        Assert.Contains("listo para medir", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopGameHealthCheckState.Ready, Check(snapshot, "executable").State);
        Assert.Equal(DesktopGameHealthCheckState.Ready, Check(snapshot, "tracking").State);
        Assert.Equal(DesktopGameHealthCheckState.Informational, Check(snapshot, "measured-history").State);
        Assert.Equal(DesktopGameHealthCheckState.Informational, Check(snapshot, "historical-recovery").State);
        Assert.Equal(DesktopGameHealthCheckState.Informational, Check(snapshot, "achievements").State);
    }

    [Fact]
    public void Build_ActiveGame_ReportsCurrentSessionWithoutAddingAnotherScanner()
    {
        var executable = CreateExecutable();
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: executable, measuredSessionCount: 2),
            isTracking: true,
            isActive: true,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopGameHealthState.Ready, snapshot.OverallState);
        Assert.Contains("sesión activa", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ahora mismo", Check(snapshot, "tracking").Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopGameHealthCheckState.Ready, Check(snapshot, "measured-history").State);
    }

    [Fact]
    public void Build_MissingExecutableAssociation_IsInformationalUntilResolverObservesTheGame()
    {
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: null),
            isTracking: true,
            isActive: false,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopGameHealthState.Ready, snapshot.OverallState);
        Assert.Contains("no hay problemas conocidos", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopGameHealthCheckState.Informational, Check(snapshot, "executable").State);
    }

    [Fact]
    public void Build_StaleExecutableAssociation_NeedsAttention()
    {
        var stalePath = Path.Combine(_directory, "missing.exe");
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: stalePath),
            isTracking: true,
            isActive: false,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopGameHealthState.NeedsAttention, snapshot.OverallState);
        Assert.Contains("ya no se encuentra", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(stalePath, Check(snapshot, "executable").Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_ActiveSession_OutweighsAStaleLearnedExecutable()
    {
        var stalePath = Path.Combine(_directory, "old-location.exe");
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: stalePath),
            isTracking: true,
            isActive: true,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopGameHealthState.Ready, snapshot.OverallState);
        Assert.Contains("sesión activa", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopGameHealthCheckState.Informational, Check(snapshot, "executable").State);
        Assert.Contains("sesión actual", Check(snapshot, "executable").Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Build_StoppedTracker_TakesPriorityOverGameSpecificChecks()
    {
        var executable = CreateExecutable();
        var snapshot = DesktopGameHealthSnapshotBuilder.Build(
            CreateGame(executablePath: executable),
            isTracking: false,
            isActive: false,
            DateTimeOffset.UtcNow);

        Assert.Equal(DesktopGameHealthState.NotTracking, snapshot.OverallState);
        Assert.Contains("seguimiento", snapshot.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(DesktopGameHealthCheckState.NeedsAttention, Check(snapshot, "tracking").State);
    }

    private string CreateExecutable()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, []);
        return path;
    }

    private static DesktopGameRow CreateGame(
        string? executablePath,
        int measuredSessionCount = 0)
    {
        var now = DateTimeOffset.Parse("2026-09-12T09:00:00Z");
        var measuredAt = measuredSessionCount > 0 ? now : (DateTimeOffset?)null;
        return new DesktopGameRow(
            Guid.NewGuid(),
            "Health Test",
            measuredSessionCount > 0 ? TimeSpan.FromHours(2) : TimeSpan.Zero,
            measuredSessionCount > 0 ? TimeSpan.FromHours(2) : TimeSpan.Zero,
            TimeSpan.Zero,
            TimeSpan.Zero,
            ActivePlaytime: null,
            ActivityMeasuredSessionCount: 0,
            FirstActivityAtUtc: measuredAt,
            LastActivityAtUtc: measuredAt,
            FirstMeasuredSessionAtUtc: measuredAt,
            LastMeasuredSessionAtUtc: measuredAt,
            MeasuredSessionCount: measuredSessionCount,
            ExecutablePath: executablePath,
            RecentSessions: [],
            ExecutableExists: executablePath is not null && File.Exists(executablePath));
    }

    private static DesktopGameHealthCheck Check(DesktopGameHealthSnapshot snapshot, string code) =>
        Assert.Single(snapshot.Checks, item => item.Code == code);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
        catch
        {
        }
    }
}
