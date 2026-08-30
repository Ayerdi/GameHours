namespace GameHours.Windows.Achievements;

/// <summary>
/// Confidence assigned to a Steam AppID identity. High-confidence identities come from
/// installation metadata or emulator configuration that explicitly names the real game.
/// Medium confidence is reserved for generic Steam override markers such as steam_appid.txt.
/// </summary>
public enum SteamAppIdConfidence
{
    Medium = 1,
    High = 2
}

/// <summary>
/// Auditable Steam AppID identity derived from local installation evidence.
/// </summary>
public sealed record SteamAppIdResolution(
    string AppId,
    string EvidenceSource,
    string? EvidencePath,
    SteamAppIdConfidence Confidence,
    bool FromPersistentCache = false);
