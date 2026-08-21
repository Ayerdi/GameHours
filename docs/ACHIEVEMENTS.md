# Local achievements

GameHours treats achievements as a local-first compatibility feature.

## Privacy and network boundary

- Achievement discovery and parsing must operate on files already present on the user's machine.
- GameHours must not call Hydra Cloud or Hydra API endpoints.
- Steam Web API access is not required for local achievement state.
- Optional online metadata, if added in the future, must be a separate provider with an explicit network boundary.

## Compatibility architecture

The Windows layer separates three concerns:

1. `LocalAchievementSourceLocator` locates known local achievement files.
2. Source-specific parsers interpret those files without modifying them.
3. `LocalAchievementProviderChain` exposes normalized achievement data to Desktop.

The provider order intentionally prefers richer local data:

1. GSE/Goldberg definitions plus user state when both are available.
2. Steam-compatible emulator/local state files.
3. Steam `librarycache` state.

This prevents a partial state file from replacing a complete local catalogue.

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

Some of these files contain only unlocked state rather than the full achievement catalogue. Such snapshots are labelled `estado parcial`; Desktop displays `N desbloqueados` instead of a misleading `N/N` total.

Recognition does not imply that every discovered format is parsed. SmartSteamEmu and any unrecognized variant remain diagnostic-only until their local format is validated.

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
