using GameHours.Core.Domain;

namespace GameHours.Tests;

public sealed class GameExternalIdentityTests
{
    [Theory]
    [InlineData(GameDiscoverySource.Steam, "3946950", GameExternalIdentityProviders.Steam)]
    [InlineData(GameDiscoverySource.Epic, "epic-catalog-offer", GameExternalIdentityProviders.Epic)]
    [InlineData(GameDiscoverySource.Gog, "1207659000", GameExternalIdentityProviders.Gog)]
    public void DiscoveredCatalogueGame_MapsToProviderScopedIdentity(
        GameDiscoverySource source,
        string externalId,
        string expectedProvider)
    {
        var game = new DiscoveredGame(
            Guid.NewGuid(),
            "External identity",
            source,
            externalId,
            Path.Combine(Path.GetTempPath(), "gamehours-identity-test"),
            launchExecutable: null,
            confidence: 1.0);

        var identity = GameExternalIdentityProviders.FromDiscoveredGame(game);

        Assert.NotNull(identity);
        Assert.Equal(expectedProvider, identity.Provider);
        Assert.Equal(externalId, identity.ExternalId);
    }

    [Fact]
    public void LooseProcess_DoesNotInventAnExternalCatalogueIdentity()
    {
        var game = new DiscoveredGame(
            Guid.NewGuid(),
            "Loose game",
            GameDiscoverySource.LooseProcess,
            "local-placeholder",
            Path.Combine(Path.GetTempPath(), "gamehours-loose-identity-test"),
            launchExecutable: null,
            confidence: 0.8);

        Assert.Null(GameExternalIdentityProviders.FromDiscoveredGame(game));
    }
}
