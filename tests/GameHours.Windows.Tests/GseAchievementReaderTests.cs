using System.Globalization;
using GameHours.Windows.Achievements;

namespace GameHours.Windows.Tests;

public sealed class GseAchievementReaderTests
{
    [Fact]
    public void TryRead_ParsesLocalCatalogWithoutUserState()
    {
        using var fixture = new TempAchievementFixture();
        fixture.WriteDefinitions("""
            [
              {
                "name": "ACH_FIRST",
                "displayName": { "english": "First", "spanish": "Primero" },
                "description": { "english": "First description", "spanish": "Primera descripción" },
                "hidden": false,
                "icon": "achievement_images/first.png",
                "icongray": "achievement_images/first_gray.png"
              },
              {
                "name": "ACH_SECRET",
                "displayName": "Secret",
                "description": "Hidden description",
                "hidden": true
              }
            ]
            """);
        fixture.WriteImage("first.png");
        fixture.WriteImage("first_gray.png");

        var previousCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
        try
        {
            var snapshot = new GseAchievementReader().TryRead(fixture.ExecutablePath);

            Assert.NotNull(snapshot);
            Assert.Equal("GSE/Goldberg local", snapshot.Source);
            Assert.Null(snapshot.StatePath);
            Assert.Equal(2, snapshot.Achievements.Count);
            Assert.Equal(0, snapshot.UnlockedCount);

            var first = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_FIRST");
            Assert.Equal("Primero", first.DisplayName);
            Assert.Equal("Primera descripción", first.Description);
            Assert.False(first.IsUnlocked);
            Assert.NotNull(first.IconPath);
            Assert.NotNull(first.LockedIconPath);

            var secret = Assert.Single(snapshot.Achievements, item => item.ApiName == "ACH_SECRET");
            Assert.True(secret.Hidden);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void TryRead_ReturnsNullForMalformedCatalog()
    {
        using var fixture = new TempAchievementFixture();
        fixture.WriteDefinitions("{ not-json");

        var snapshot = new GseAchievementReader().TryRead(fixture.ExecutablePath);

        Assert.Null(snapshot);
    }

    [Fact]
    public void ProviderChain_UsesFirstProviderThatCanReadGame()
    {
        var expected = new LocalAchievementSnapshot(
            "second",
            null,
            "definitions.json",
            null,
            Array.Empty<LocalAchievement>());
        var chain = new LocalAchievementProviderChain(new ILocalAchievementProvider[]
        {
            new StubProvider("first", null),
            new StubProvider("second", expected),
            new StubProvider("third", throwIfCalled: true)
        });

        var result = chain.TryRead(@"C:\Games\example.exe");

        Assert.Same(expected, result);
    }

    private sealed class StubProvider : ILocalAchievementProvider
    {
        private readonly LocalAchievementSnapshot? _snapshot;
        private readonly bool _throwIfCalled;

        public StubProvider(string name, LocalAchievementSnapshot? snapshot = null, bool throwIfCalled = false)
        {
            Name = name;
            _snapshot = snapshot;
            _throwIfCalled = throwIfCalled;
        }

        public string Name { get; }

        public LocalAchievementSnapshot? TryRead(string executablePath)
        {
            if (_throwIfCalled)
            {
                throw new InvalidOperationException("Provider should not have been called.");
            }

            return _snapshot;
        }
    }

    private sealed class TempAchievementFixture : IDisposable
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(),
            "GameHours.Tests",
            Guid.NewGuid().ToString("N"));

        public TempAchievementFixture()
        {
            Directory.CreateDirectory(GameDirectory);
            Directory.CreateDirectory(SettingsDirectory);
            Directory.CreateDirectory(ImageDirectory);
            File.WriteAllBytes(ExecutablePath, Array.Empty<byte>());
        }

        private string GameDirectory => Path.Combine(_root, "game");
        private string SettingsDirectory => Path.Combine(GameDirectory, "steam_settings");
        private string ImageDirectory => Path.Combine(SettingsDirectory, "achievement_images");
        public string ExecutablePath => Path.Combine(GameDirectory, "game.exe");

        public void WriteDefinitions(string json) =>
            File.WriteAllText(Path.Combine(SettingsDirectory, "achievements.json"), json);

        public void WriteImage(string fileName) =>
            File.WriteAllBytes(Path.Combine(ImageDirectory, fileName), new byte[] { 1, 2, 3 });

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
}
