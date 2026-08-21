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

The source locator currently recognizes local paths used by Steam-compatible caches/emulators including:

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

Recognition does not imply that every format is already parsed. Locating and parsing are intentionally separate so unsupported sources can be surfaced diagnostically without guessing their contents.

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
