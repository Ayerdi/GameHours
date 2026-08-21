using System.Text;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class SteamLocalStatsAchievementReaderTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "GameHours.SteamStats.Tests",
        Guid.NewGuid().ToString("N"));

    public SteamLocalStatsAchievementReaderTests()
    {
        Directory.CreateDirectory(_root);
    }

    [Fact]
    public void TryReadFiles_JoinsSchemaBitsWithUserMaskAndUnlockTimes()
    {
        var schemaPath = Path.Combine(_root, "UserGameStatsSchema_123456.bin");
        var userPath = Path.Combine(_root, "UserGameStats_42_123456.bin");
        WriteSchema(schemaPath);
        WriteUserStats(userPath);

        var snapshot = new SteamLocalStatsAchievementReader().TryReadFiles(
            schemaPath,
            userPath,
            "123456");

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsCatalogueComplete);
        Assert.Equal("Steam local stats", snapshot.Source);
        Assert.Equal("123456", snapshot.AppId);
        Assert.Equal(2, snapshot.Achievements.Count);
        Assert.Equal(1, snapshot.UnlockedCount);

        var first = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_FIRST");
        Assert.Equal("First achievement", first.DisplayName);
        Assert.Equal("Do the first thing", first.Description);
        Assert.True(first.IsUnlocked);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000), first.UnlockedAtUtc);

        var secret = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_SECRET");
        Assert.True(secret.Hidden);
        Assert.False(secret.IsUnlocked);
        Assert.Null(secret.UnlockedAtUtc);
    }

    [Fact]
    public void TryReadFiles_CompleteCatalogueStillLoadsWithoutUserState()
    {
        var schemaPath = Path.Combine(_root, "UserGameStatsSchema_123456.bin");
        WriteSchema(schemaPath);

        var snapshot = new SteamLocalStatsAchievementReader().TryReadFiles(
            schemaPath,
            userStatsPath: null,
            appId: "123456");

        Assert.NotNull(snapshot);
        Assert.True(snapshot.IsCatalogueComplete);
        Assert.Equal(2, snapshot.Achievements.Count);
        Assert.Equal(0, snapshot.UnlockedCount);
        Assert.Null(snapshot.StatePath);
    }

    [Fact]
    public void TryReadFiles_ReturnsNullForMalformedBinaryKeyValues()
    {
        var schemaPath = Path.Combine(_root, "UserGameStatsSchema_123456.bin");
        File.WriteAllBytes(schemaPath, new byte[] { 0, 65, 0, 1, 66 });

        Assert.Null(new SteamLocalStatsAchievementReader().TryReadFiles(
            schemaPath,
            userStatsPath: null,
            appId: "123456"));
    }

    private static void WriteSchema(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        WriteObject(writer, "123456", () =>
        {
            WriteObject(writer, "stats", () =>
            {
                WriteObject(writer, "10", () =>
                {
                    WriteString(writer, "type", "ACHIEVEMENTS");
                    WriteObject(writer, "bits", () =>
                    {
                        WriteObject(writer, "0", () =>
                        {
                            WriteString(writer, "name", "ACH_FIRST");
                            WriteDisplay(
                                writer,
                                "First achievement",
                                "Do the first thing",
                                hidden: false);
                        });

                        WriteObject(writer, "1", () =>
                        {
                            WriteString(writer, "name", "ACH_SECRET");
                            WriteDisplay(
                                writer,
                                "Secret achievement",
                                "A hidden thing",
                                hidden: true);
                        });
                    });
                });
            });
        });

        WriteEnd(writer);
    }

    private static void WriteUserStats(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);

        WriteObject(writer, "cache", () =>
        {
            WriteObject(writer, "10", () =>
            {
                WriteInt32(writer, "data", 1);
                WriteObject(writer, "AchievementTimes", () =>
                {
                    WriteInt32(writer, "0", 1_700_000_000);
                    WriteInt32(writer, "1", 0);
                });
            });
        });

        WriteEnd(writer);
    }

    private static void WriteDisplay(
        BinaryWriter writer,
        string name,
        string description,
        bool hidden)
    {
        WriteObject(writer, "display", () =>
        {
            WriteObject(writer, "name", () => WriteString(writer, "english", name));
            WriteObject(writer, "desc", () => WriteString(writer, "english", description));
            WriteInt32(writer, "hidden", hidden ? 1 : 0);
            WriteString(writer, "icon", "normal_hash");
            WriteString(writer, "icon_gray", "locked_hash");
        });
    }

    private static void WriteObject(BinaryWriter writer, string name, Action body)
    {
        writer.Write((byte)0);
        WriteNullTerminatedUtf8(writer, name);
        body();
        WriteEnd(writer);
    }

    private static void WriteString(BinaryWriter writer, string name, string value)
    {
        writer.Write((byte)1);
        WriteNullTerminatedUtf8(writer, name);
        WriteNullTerminatedUtf8(writer, value);
    }

    private static void WriteInt32(BinaryWriter writer, string name, int value)
    {
        writer.Write((byte)2);
        WriteNullTerminatedUtf8(writer, name);
        writer.Write(value);
    }

    private static void WriteEnd(BinaryWriter writer) => writer.Write((byte)8);

    private static void WriteNullTerminatedUtf8(BinaryWriter writer, string value)
    {
        writer.Write(Encoding.UTF8.GetBytes(value));
        writer.Write((byte)0);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }
}
