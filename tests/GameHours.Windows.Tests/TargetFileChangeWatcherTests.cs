using GameHours.Windows.IO;

namespace GameHours.Windows.Tests;

public sealed class TargetFileChangeWatcherTests
{
    [Fact]
    public async Task WaitAsync_TargetFileChange_WakesWatcher()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(directory, "achievements.json");
            await File.WriteAllTextAsync(target, "before");
            using var watcher = TargetFileChangeWatcher.TryCreate(target);
            Assert.NotNull(watcher);

            var waitTask = watcher!.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
            await Task.Delay(50);
            await File.AppendAllTextAsync(target, " after");

            var reason = await waitTask;
            Assert.Equal(TargetFileWakeReason.Changed, reason);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public async Task WaitAsync_UnrelatedFileChange_DoesNotWakeTargetWatcher()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(directory, "achievements.json");
            var unrelated = Path.Combine(directory, "other.json");
            await File.WriteAllTextAsync(target, "target");
            await File.WriteAllTextAsync(unrelated, "other");
            using var watcher = TargetFileChangeWatcher.TryCreate(target);
            Assert.NotNull(watcher);

            await File.AppendAllTextAsync(unrelated, " changed");
            var reason = await watcher!.WaitAsync(TimeSpan.FromMilliseconds(250), CancellationToken.None);

            Assert.Equal(TargetFileWakeReason.Fallback, reason);
        }
        finally
        {
            DeleteTemporaryDirectory(directory);
        }
    }

    [Fact]
    public void TryCreate_MissingDirectory_ReturnsNull()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"gamehours-missing-{Guid.NewGuid():N}",
            "achievements.json");

        using var watcher = TargetFileChangeWatcher.TryCreate(path);

        Assert.Null(watcher);
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"gamehours-watcher-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void DeleteTemporaryDirectory(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch
        {
        }
    }
}
