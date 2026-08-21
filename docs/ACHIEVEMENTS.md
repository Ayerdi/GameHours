# Local achievements

GameHours treats achievements as a local-first compatibility feature.

## Privacy and network boundary

- Achievement discovery and parsing must operate on files already present on the user's machine.
- GameHours must not call Hydra Cloud or Hydra API endpoints.
- Steam Web API access is not required for local achievement state.
- Optional online metadata, if added in the future, must be a separate provider with an explicit network boundary.

## Compatibility architecture

The Windows layer separates four concerns:

1. `LocalAchievementSourceLocator` locates known local achievement files.
2. Source-specific parsers interpret those files without modifying them.
3. `AggregatingLocalAchievementProvider` combines complete catalogues with unlock state from any compatible local source.
4. `LocalAchievementObservationService` reconciles the resulting snapshot with GameHours persistence and identifies later locked-to-unlocked transitions.

A complete local catalogue always wins for metadata and total-count purposes. Partial state sources can enrich that catalogue with unlocks, but unknown IDs from a partial source do not inflate a complete catalogue total.

When no complete catalogue is available, partial sources are unioned by achievement API name and GameHours reports only `N desbloqueados`, never a misleading `N/N` total.

## Recognized local sources

The source locator recognizes local paths used by Steam-compatible caches/emulators including:

- Steam `userdata/<user>/config/librarycache/<appid>.json`
- Goldberg / GSE
- CODEX
- RUNE
- OnlineFix
- EMPRESS
- RLD
- SKIDROW
- CreamAPI
- SmartSteamEmu
- RLE
- Razor1911
- game-directory `SteamData/user_stats.ini`
- game-directory 3DM profiles
- game-directory ALI213 profiles
- `steam_settings` achievement definitions

For installed Steam games, GameHours can also resolve the AppID by matching the remembered executable against local Steam `appmanifest_*.acf` files. This remains entirely local.

## Parsed formats

GameHours currently parses:

- GSE/Goldberg JSON definitions and state, including names, descriptions, hidden flags, progress, artwork and unlock timestamps.
- Steam `librarycache` JSON achievement state (`strID`, `bAchieved`, `rtUnlocked`).
- CODEX/RUNE/RLE-style INI state (`Achieved`, `UnlockTime`).
- OnlineFix INI variants.
- CreamAPI INI state.
- SKIDROW `achiev.ini` state.
- EMPRESS Goldberg-like JSON state.
- `SteamData/user_stats.ini` state.
- 3DM state/time INI data.
- RLD state/time INI data.
- ALI213 `HaveAchieved` state when the local file is text-compatible.
- Razor1911 line-based state.

Recognition does not imply that every discovered format is parsed. SmartSteamEmu and any unrecognized variant remain diagnostic-only until their local format is validated.

## Aggregation rules

`LocalAchievementSnapshotMerger` applies conservative rules when multiple sources describe the same game:

- complete catalogue metadata is preserved;
- an achievement is considered unlocked if any compatible state source marks it unlocked;
- the earliest valid known unlock timestamp is retained when sources disagree;
- partial-source IDs that do not exist in a complete catalogue are ignored for the catalogue total;
- if no complete catalogue exists, unlocked IDs from partial sources are deduplicated and unioned.

This allows combinations such as `steam_settings/achievements.json` for catalogue metadata plus CODEX/RUNE/OnlineFix/Steam local state without treating those formats as separate games.

## Persistent local state

GameHours stores normalized achievement observations in the local SQLite database (`achievement_states`). Persistence is monotonic for unlock state:

- once an achievement has been observed unlocked, a later incomplete source cannot relock it;
- richer catalogue metadata is not overwritten by API-name-only partial state;
- the earliest known source unlock timestamp is preserved;
- `first_seen_at_utc`, `last_seen_at_utc` and `first_unlocked_seen_at_utc` are retained separately.

The first successful observation for a game is treated as an achievement baseline. Existing historical unlocks are stored but are not candidates for "new achievement" notifications. Later locked-to-unlocked transitions are returned as notification candidates.

The game-detail view currently reconciles each successful local read into this persistent state. Notification presentation itself is intentionally deferred until the persistence/transition layer has been validated on a real machine.

## Live refresh

When a game detail view has identified a concrete local achievement-state file, Desktop watches only that file with `FileSystemWatcher`. Changes are debounced before reparsing. The manual `Actualizar logros` action remains available as a fallback.

The watcher is read-only and does not scan the whole filesystem continuously.

## Attribution and implementation policy

The compatibility matrix was informed by the public Hydra Launcher achievement implementation (`hydralauncher/hydra`), which is distributed under the MIT License.

GameHours does not use Hydra Cloud and does not depend on Hydra at runtime. The GameHours implementation is written independently in C# around local file formats and Windows paths. If a future change incorporates a substantial portion of third-party source rather than independently implementing the format, the corresponding copyright and license notice must be preserved in the repository/distribution as required by that project's license.

Reference:
- https://github.com/hydralauncher/hydra
- https://github.com/hydralauncher/hydra/blob/main/LICENSE

## Current validation

Project P.I.T.T. has been validated on a real Windows machine using local GSE/Goldberg files:

- definitions: `steam_settings/achievements.json`
- state: `%APPDATA%/GSE Saves/4026250/achievements.json`
- result: 4 of 23 achievements unlocked with local unlock timestamps

No remote API was involved in that validation.

The newer aggregation, multi-format parsers, SQLite persistence and transition-detection layers are implemented with automated tests but remain pending real-machine validation.
