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
/// Steam-compatible runtime family when local identity evidence is specific enough to name it.
/// Unknown is intentional for generic markers such as steam_appid.txt and steam_emu.ini: those
/// files can be used by several unrelated runtimes, so GameHours must not guess a family.
/// </summary>
public enum SteamRuntimeFamily
{
    Unknown = 0,
    OfficialSteam,
    GoldbergGse,
    OnlineFix,
    Codex,
    Rune,
    Empress,
    Rld,
    Skidrow,
    CreamApi,
    SmartSteamEmu,
    Rle,
    Razor1911,
    UserStats,
    ThreeDm,
    Ali213,
    Cpy,
    Tenoke
}

/// <summary>
/// Auditable Steam AppID identity derived from local installation evidence.
/// RuntimeFamily is derived from the evidence label instead of being persisted separately, so
/// existing machine-local identity caches remain backwards compatible and current evidence rules
/// stay the single source of truth.
/// </summary>
public sealed record SteamAppIdResolution(
    string AppId,
    string EvidenceSource,
    string? EvidencePath,
    SteamAppIdConfidence Confidence,
    bool FromPersistentCache = false)
{
    public SteamRuntimeFamily RuntimeFamily => EvidenceSource switch
    {
        "Steam appmanifest" => SteamRuntimeFamily.OfficialSteam,
        "Goldberg/GBE steam_settings AppID" => SteamRuntimeFamily.GoldbergGse,
        "OnlineFix RealAppId" => SteamRuntimeFamily.OnlineFix,
        "SmartSteamEmu AppId" => SteamRuntimeFamily.SmartSteamEmu,
        "CPY AppID" => SteamRuntimeFamily.Cpy,
        "TENOKE id" => SteamRuntimeFamily.Tenoke,
        _ => SteamRuntimeFamily.Unknown
    };
}
