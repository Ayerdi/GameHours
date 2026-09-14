using System.IO.Compression;
using GameHours.Desktop;
using GameHours.Windows.Processes;

namespace GameHours.Windows.Tests;

public sealed class DesktopDiagnosticPackageBuilderTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "GameHours.Windows.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CreateAsync_WritesOnlySafeGeneratedFilesAndRedactsUserPaths()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "diagnostic.zip");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var runtime = CreateRuntime(
            databasePath: Path.Combine(profile, "private", "gamehours.db"),
            preferencesPath: Path.Combine(profile, "private", "settings.json"));

        await DesktopDiagnosticPackageBuilder.CreateAsync(new(
            destination,
            "1.2.3",
            "beta",
            runtime,
            $"Problem in {profile} for {Environment.UserName}; token=abc123 Authorization: Bearer xyz.123"));

        using var archive = ZipFile.OpenRead(destination);
        var entries = archive.Entries.Select(entry => entry.FullName).ToHashSet(StringComparer.Ordinal);
        Assert.True(entries.SetEquals(["README.txt", "problem.txt", "runtime.json", "summary.json"]));

        var combined = string.Join("\n", archive.Entries.Select(ReadEntry));
        Assert.DoesNotContain(profile, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("abc123", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("xyz.123", combined, StringComparison.Ordinal);
        Assert.Contains("token=<REDACTED>", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Bearer <REDACTED>", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("<USERPROFILE>", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("gamehours.db", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settings.json", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"schemaVersion\": 7", combined, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateAsync_OmitsOptionalProblemFileWhenDescriptionIsBlank()
    {
        Directory.CreateDirectory(_directory);
        var destination = Path.Combine(_directory, "diagnostic.zip");

        await DesktopDiagnosticPackageBuilder.CreateAsync(new(
            destination,
            "dev",
            "development",
            CreateRuntime(),
            "  "));

        using var archive = ZipFile.OpenRead(destination);
        Assert.DoesNotContain(archive.Entries, entry => entry.FullName == "problem.txt");
    }

    private static DesktopRuntimeDiagnostics CreateRuntime(
        string databasePath = "C:\\safe\\gamehours.db",
        string preferencesPath = "C:\\safe\\settings.json") =>
        new(
            IsTracking: true,
            StatusText: "Playing a private title",
            ActiveGameTitle: "Private title",
            Preferences: DesktopPreferences.Default,
            AppliedAfkTimeoutMinutes: 5,
            ProcessMonitor: new WindowsProcessMonitorDiagnostics(true, true, false, 3, 4, DateTimeOffset.UtcNow),
            ProcessCpuTime: TimeSpan.FromSeconds(3),
            PrivateMemoryBytes: 10,
            WorkingSetBytes: 20,
            ThreadCount: 4,
            ManagedHeapBytes: 30,
            TotalAllocatedBytes: 40,
            Gen0CollectionCount: 1,
            Gen1CollectionCount: 2,
            Gen2CollectionCount: 3,
            GcCommittedBytes: 50,
            GcFragmentedBytes: 5,
            GcTotalPauseDuration: TimeSpan.FromMilliseconds(20),
            DatabasePath: databasePath,
            PreferencesPath: preferencesPath,
            DatabaseSchemaVersion: 7);

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var reader = new StreamReader(entry.Open());
        return reader.ReadToEnd();
    }

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
