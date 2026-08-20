namespace GameHours.Core.Domain;

public enum GameDiscoverySource
{
    Steam = 1,
    Epic = 2,
    Gog = 3,
    LooseProcess = 4
}

public sealed record DiscoveredGame
{
    public Guid GameId { get; }
    public string Title { get; }
    public GameDiscoverySource Source { get; }
    public string ExternalId { get; }
    public string InstallDirectory { get; }
    public string? LaunchExecutable { get; }
    public double Confidence { get; }

    public DiscoveredGame(
        Guid gameId,
        string title,
        GameDiscoverySource source,
        string externalId,
        string installDirectory,
        string? launchExecutable,
        double confidence)
    {
        if (gameId == Guid.Empty) throw new ArgumentException("Game id cannot be empty.", nameof(gameId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Game title cannot be empty.", nameof(title));
        if (string.IsNullOrWhiteSpace(externalId)) throw new ArgumentException("External id cannot be empty.", nameof(externalId));
        if (string.IsNullOrWhiteSpace(installDirectory)) throw new ArgumentException("Install directory cannot be empty.", nameof(installDirectory));
        if (confidence is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(confidence));

        GameId = gameId;
        Title = title.Trim();
        Source = source;
        ExternalId = externalId.Trim();
        InstallDirectory = Path.GetFullPath(installDirectory);
        LaunchExecutable = string.IsNullOrWhiteSpace(launchExecutable) ? null : launchExecutable.Trim();
        Confidence = confidence;
    }

    public TrackedGame ToTrackedGame() => new(GameId, Title);
}
