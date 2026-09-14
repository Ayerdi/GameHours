namespace GameHours.Core.Domain;

/// <summary>
/// Stable identity assigned by an external catalogue/platform. GameHours keeps its own UUID as
/// the tracking identity; these values exist only to correlate that UUID with optional sources.
/// </summary>
public sealed record GameExternalIdentity
{
    public string Provider { get; }
    public string ExternalId { get; }

    public GameExternalIdentity(string provider, string externalId)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            throw new ArgumentException("External identity provider cannot be empty.", nameof(provider));
        }

        if (string.IsNullOrWhiteSpace(externalId))
        {
            throw new ArgumentException("External identity value cannot be empty.", nameof(externalId));
        }

        Provider = provider.Trim().ToLowerInvariant();
        ExternalId = externalId.Trim();
    }
}

public static class GameExternalIdentityProviders
{
    public const string Steam = "steam";
    public const string Epic = "epic";
    public const string Gog = "gog";
    public const string Igdb = "igdb";

    public static GameExternalIdentity? FromDiscoveredGame(DiscoveredGame game)
    {
        ArgumentNullException.ThrowIfNull(game);
        var provider = game.Source switch
        {
            GameDiscoverySource.Steam => Steam,
            GameDiscoverySource.Epic => Epic,
            GameDiscoverySource.Gog => Gog,
            _ => null
        };

        return provider is null ? null : new GameExternalIdentity(provider, game.ExternalId);
    }
}
