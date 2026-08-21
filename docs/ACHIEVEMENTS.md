# Local achievements

GameHours treats achievements as a local-first compatibility feature.

## Privacy and network boundary

- Achievement discovery and parsing must operate on files already present on the user's machine.
- GameHours must not call Hydra Cloud or Hydra API endpoints.
- Steam Web API access is not required for local achievement state.
- Optional online metadata, if added in the future, must be a separate provider with an explicit network boundary.

## Compatibility architecture

The Windows layer separates five concerns:

1. `LocalAchievementSourceLocator` locates known local achievement files.
2. Source-specific parsers interpret those files without modifying them.
3. `AggregatingLocalAchievementProvider` combines complete catalogues with unlock state from compatible local sources.
4. `LocalAchievementObservationService` reconciles the resulting snapshot with GameHours persistence and identifies locked-to-unlocked transitions.
5. Desktop session monitoring decides whether a transition is recent enough and contextually safe to present as a live notification.

A complete local catalogue always wins for metadata and total-count purposes. Partial state sources can enrich that catalogue with unlocks, but unknown IDs from a partial source do not inflate a complete catalogue total.

When no complete catalogue is available, partial sources are unioned by achievement API name and GameHours reports only `N desbloqueados`, never a misleading `N/N` total.

Official Steam installations and Steam-compatible emulator saves are deliberately isolated. GameHours does not merge CODEX/GSE/etc. state into an executable that is resolved as belonging to an installed Steam library merely because both sources share the same AppID.

## Recognized local sources

The source locator recognizes local paths used by Steam-compatible caches/emulators including:

- Steam `userdata/<user>/config/librarycache/<appid>.json`
- Steam `appcache/stats/UserGameStatsSchema_<appid>.bin`
- Steam `appcache/stats/UserGameStats_<account>_<appid>.bin`
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

For installed Steam games, GameHours resolves the AppID by matching the remembered executable against local Steam `appmanifest_*.acf` files. This remains entirely local.

## Parsed formats

GameHours currently parses:

- GSE/Goldberg JSON definitions and state, including names, descriptions, hidden flags, progress, artwork and unlock timestamps.
- Steam Binary KeyValues achievement schema and per-user state from `appcache/stats`, including the complete local catalogue, bit-mask unlock state and `AchievementTimes` timestamps when present.
- Steam `librarycache` JSON achievement state (`strID`, `bAchieved`, `rtUnlocked`) as a partial fallback.
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

### Steam account safety

Steam's `appcache/stats` can contain data for more than one account. GameHours first tries the locally active Steam account. If that cannot be determined, a user-state file is accepted only when exactly one matching account file exists. Multiple ambiguous account files are never merged or guessed; in that case GameHours may still show the local catalogue without attaching user unlock state.

## Aggregation rules

`LocalAchievementSnapshotMerger` applies conservative rules when multiple compatible sources describe the same installation:

- complete catalogue metadata is preserved;
- an achievement is considered unlocked if any compatible state source marks it unlocked;
- the earliest valid known unlock timestamp is retained when sources disagree;
- partial-source IDs that do not exist in a complete catalogue are ignored for the catalogue total;
- if no complete catalogue exists, unlocked IDs from partial sources are deduplicated and unioned.

This allows combinations such as `steam_settings/achievements.json` for catalogue metadata plus a compatible local emulator state without treating those formats as separate games.

## Persistent local state

GameHours stores normalized achievement observations in the local SQLite database (`achievement_states`). Persistence is monotonic for unlock state:

- once an achievement has been observed unlocked, a later incomplete source cannot relock it;
- richer catalogue metadata is not overwritten by API-name-only partial state;
- the earliest known source unlock timestamp is preserved;
- `first_seen_at_utc`, `last_seen_at_utc` and `first_unlocked_seen_at_utc` are retained separately.

`achievement_observation_state` records that a game has had at least one readable achievement observation even when that first snapshot contains zero unlocked achievements. This lets later unlocks be distinguished from the initial historical baseline.

`SqliteAchievementActivityRepository` is the read-only view over that normalized state. It can return:

- per-game known/unlocked counts;
- whether the count comes from a complete catalogue or only observed IDs;
- first and last best-known unlock occurrence;
- last local achievement observation/source;
- recent unlock activity globally or filtered by game.

For activity timestamps, GameHours prefers the source's real unlock time. If a format has no reliable timestamp it falls back to `first_unlocked_seen_at_utc` and exposes `IsObservedTimeFallback=true`, so the UI can say “detectado” rather than pretending the time is exact.

## Live session monitoring and notifications

Achievement persistence and notification detection are tied to GameHours measured sessions, not to the game-detail window.

On `SessionStarted` Desktop resolves the remembered executable and starts one achievement monitor for that game. The monitor:

1. attempts an immediate local observation near session start;
2. fingerprints the concrete state file once per second using path, existence, length and last-write time;
3. reparses only when the fingerprint changes;
4. performs a low-frequency full re-read as a fallback and to discover a state file that did not exist at game start;
5. performs an immediate reconciliation on `SessionCompleted` and another bounded retry about 450 ms later for formats that finish flushing state just after process exit.

`AchievementSessionNotificationGate` applies additional conservative rules:

- a first-ever persistent observation is always historical baseline and never notifies;
- a first readable observation that occurs immediately after session start is also a silent session baseline;
- when GameHours already had a durable baseline, a first readable observation arriving later in the session may surface a transition, which supports emulators that only flush achievement files at exit;
- a given API name can be emitted at most once per session;
- a candidate carrying an unlock timestamp clearly older than the measured session is rejected;
- missing timestamps remain eligible after the baseline because several local formats do not record reliable unlock times.

The host emits a transport-neutral `AchievementUnlocked` event. Desktop currently presents that event through the existing notification-area icon as a conservative balloon fallback. A modern Windows toast transport can replace/augment that presentation later without changing detection or persistence.

The game-detail view still watches its currently displayed state file with `FileSystemWatcher` so the visible list refreshes quickly, but it no longer owns achievement persistence. This avoids a foreground UI refresh racing the background monitor and consuming a new-unlock transition before the notification pipeline sees it.

## Design references and license boundary

GameHours studies public projects to understand file formats, compatibility patterns and useful product behavior. Reference does not mean source copying.

### Hydra Launcher (`hydralauncher/hydra`) — MIT

Useful ideas:

- broad local compatibility/source matrix;
- separate source discovery from parsing;
- normalize different local achievement formats into one model;
- watch for changed achievement state rather than treating achievements as a one-time import.

GameHours does not call Hydra Cloud, does not depend on Hydra at runtime and does not copy Hydra's TypeScript implementation.

### Achievement Watcher (`xan105/Achievement-Watcher`) — LGPL-3.0

Useful behavioral ideas:

- file-change driven live detection;
- compare against previously cached state before notifying;
- process/session context and timestamp checks to reduce stale notifications;
- deduplication/spam protection;
- keep notification transport separate from achievement parsing;
- handle formats that only persist achievement changes when the game exits;
- optional progress notifications as a possible future feature.

GameHours does not copy its watchdog/parser implementation. The equivalent GameHours behavior is implemented independently around `GameSessionEngine`, SQLite persistence and C# providers.

### SuccessStory for Playnite (`Lacro59/playnite-successstory-plugin`) — MIT

Useful product/architecture ideas:

- treat achievements as persistent game data rather than transient alerts;
- separate data/services/views;
- resolve achievement artwork independently from achievement state;
- expose completion-oriented game views.

GameHours keeps its own domain model and UI; SuccessStory is a product/reference point rather than a runtime dependency.

### Steam Achievement Manager (`gibbed/SteamAchievementManager`) — permissive zlib-style license

Useful format reference:

- Steam Binary KeyValues type layout;
- `UserGameStatsSchema_<appid>.bin` catalogue structure (`stats` / `bits` / display metadata);
- relationship between schema bit positions and Steam achievement definitions.

GameHours uses an independently written, bounded Binary KeyValues reader rather than importing SAM source.

### SamRewritten (`PaulCombal/SamRewritten`) — GPL-3.0

Useful format validation:

- confirms the on-disk `UserGameStats_<account>_<appid>.bin` cache relationship;
- confirms `cache/<group>/data` bit masks and `AchievementTimes` semantics;
- documents cases where local Steam schema/state can be incomplete and should have fallbacks.

Because SamRewritten is GPL-3.0, GameHours uses it only as behavioral/format documentation and does not copy its implementation.

### Heroic Games Launcher — GPL-3.0

Useful general launcher reference:

- multi-store/provider boundaries;
- keeping platform-specific integration behind service layers rather than leaking it into the UI.

No Heroic code is incorporated into GameHours.

If a future change incorporates a substantial portion of third-party source rather than independently implementing a public file format or behavior, the corresponding copyright and license notice must be preserved as required by that project's license.

References:

- https://github.com/hydralauncher/hydra
- https://github.com/xan105/Achievement-Watcher
- https://github.com/Lacro59/playnite-successstory-plugin
- https://github.com/gibbed/SteamAchievementManager
- https://github.com/PaulCombal/SamRewritten
- https://github.com/Heroic-Games-Launcher/HeroicGamesLauncher

## Current validation

Project P.I.T.T. has been validated on a real Windows machine using local GSE/Goldberg files:

- definitions: `steam_settings/achievements.json`
- state: `%APPDATA%/GSE Saves/4026250/achievements.json`
- result: 4 of 23 achievements unlocked with local unlock timestamps

No remote API was involved in that validation.

The newer aggregation, Steam Binary KeyValues reader, multi-format parsers, SQLite persistence/activity read model, active-session monitor and notification pipeline are implemented with synthetic/unit coverage where practical but remain pending build and real-machine validation.
