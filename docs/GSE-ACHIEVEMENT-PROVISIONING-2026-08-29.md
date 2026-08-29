# GSE/Goldberg achievement provisioning — 2026-08-29

## Status

`REVIEW_REQUIRED` until the exact final SHA has completed Windows CI and the real-machine checks below are run.

This batch was explicitly authorized after two real games failed to expose local achievements through GameHours: `Click the Button` and `Big Walk`.

## Evidence and root cause

The earlier `Click the Button` investigation was correct about the local evidence that existed at the time: the installation had GSE/Goldberg configuration and Steam AppID `3946950`, but neither a local `steam_settings/achievements.json` catalogue nor persisted user unlock state. Broader filesystem searching could not recover data that had never been written.

Research of current Hydra Launcher and current GSE/Goldberg source clarified the missing lifecycle step:

- Hydra PR `hydralauncher/hydra#2697` generates missing emulator achievement metadata because Steam emulators only persist unlocks for achievements declared in `steam_settings/achievements.json`.
- GSE's achievement implementation loads its achievement database from the settings catalogue and resolves unlock calls by achievement API name. Missing icon metadata does not prevent the achievement from being defined or unlocked.
- Valve documents `ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2` with only `gameid` as a required argument. It exposes public global achievement API names without requiring a user or publisher Web API key.
- Valve documents `GetSchemaForGame` as requiring an API key, so GameHours does not make that endpoint a client-side dependency.

The product conclusion is therefore:

1. local user state remains authoritative for whether an achievement is unlocked;
2. a remote catalogue may supply definition names only;
3. GameHours may safely seed a missing GSE catalogue so the emulator can record **future** unlocks;
4. catalogue data must never be interpreted as user unlock state;
5. historical unlocks that GSE never persisted cannot be reconstructed from the catalogue.

## Implementation

### Bounded Steam-emulator settings discovery

`SteamSettingsDirectoryLocator` centralizes the game-local search instead of maintaining independent parent-walking implementations.

It:

- resolves a conservative game search root;
- covers flat layouts plus common Unreal, Unity and Steamworks executable layouts;
- checks both `steam_settings` and `coldclient/steam_settings` fast paths;
- uses a breadth-first fallback only when fast paths fail;
- limits the fallback to depth 8 and 2,000 visited directories;
- skips high-volume irrelevant content directories;
- does not follow reparse-point directories;
- never intentionally broadens into a sibling games directory.

Both the GSE catalogue reader and modern GSE runtime-state locator now reuse this discovery.

### Shared GSE recognition

`GseInstallationDetector` recognizes conservative GSE/Goldberg markers such as:

- `configs.user.ini`;
- `configs.main.ini`;
- `configs.app.ini`;
- `steam_interfaces.txt`;
- or a `steam_appid.txt` settings layout paired with a Steam API DLL at the emulator root.

The support diagnostic and the provisioner use the same detector.

### Public catalogue-name source

`SteamGlobalAchievementNameClient` requests only:

`ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2/?gameid=<appid>`

Only achievement API names are consumed. No SteamID, path, username, local state or GameHours database data is sent.

Network failure, malformed responses and an empty response are treated as catalogue unavailable. GameHours never creates an empty `achievements.json` as a substitute.

The existing richer Steam metadata cache remains presentation-only and optional; it is not required for GSE to persist unlocks.

### Safe catalogue provisioning

`GseAchievementCatalogueProvisioner` runs only when:

- a compatible GSE/Goldberg settings directory was found inside the resolved game root;
- `achievements.json` does not already exist;
- a numeric Steam AppID can be resolved;
- Steam returns at least one achievement API name.

The generated entries intentionally contain only the metadata required for correct achievement identity:

- `name`;
- `displayName` initially equal to the API name;
- empty `description`;
- `hidden = 0`.

GameHours does not download achievement images into the game installation. Its own metadata/artwork layer remains responsible for richer presentation.

Writing is fail-safe:

- existing `achievements.json` is never overwritten;
- content is written to a uniquely named temporary file with `FileMode.CreateNew`;
- the temporary file is moved into place only after serialization completes;
- a race where another process creates the destination is treated as `AlreadyPresent`;
- temporary files are cleaned up best-effort;
- the provisioner never writes user achievement state.

### UX

When the game-detail view encounters a GSE/Goldberg installation with neither catalogue nor runtime state, it now:

1. explains that GSE/Goldberg was detected;
2. visibly reports that GameHours is preparing compatibility;
3. performs discovery/network/file work away from the WPF UI thread;
4. reloads the local achievement provider if provisioning succeeds;
5. explains that the emulator can start recording those achievements from the next game start;
6. leaves the existing missing-data state intact on failure and allows a manual retry through `Actualizar`.

The achievement provider and active monitor themselves remain local-only. No network request was inserted into their periodic observation path.

## Automated coverage added

Focused tests cover:

- the real `Click the Button`-style GSE layout with AppID `3946950`;
- minimal catalogue generation without fabricating unlocked state;
- case-insensitive deduplication of Steam API names;
- no icon requirement in the generated GSE file;
- no overwrite and no remote fetch when a catalogue already exists;
- refusal to modify a generic `steam_settings` folder that is not recognized as GSE/Goldberg;
- `coldclient/steam_settings` discovery;
- sibling-game isolation;
- refusal to create an empty definitions file when the public catalogue is unavailable;
- parsing of Valve's global-achievement response shape, including a `0.0` percentage entry;
- recognition of the classic `steam_interfaces.txt` Goldberg marker.

## Real-Windows validation required

### Click the Button

1. Update/build the final reviewed SHA.
2. Ensure the existing test installation still has no `steam_settings/achievements.json`.
3. Open `Click the Button` in GameHours.
4. Confirm the UI briefly reports GSE catalogue preparation and then shows a non-empty achievement catalogue.
5. Confirm `steam_settings/achievements.json` was created inside the target game, not a sibling directory.
6. Close the game if it was already running, then start it again so GSE loads the new definitions from process initialization.
7. Earn a **new** achievement if a convenient one remains.
8. Confirm GSE creates/updates local runtime state and GameHours detects that unlock.
9. Do not expect achievements earned before the catalogue existed to appear unless the game/emulator itself later writes them.

### Big Walk

1. Open its detail after updating to the final SHA.
2. If GameHours detects GSE/Goldberg, confirm the same preparation flow and repeat a fresh-unlock test after restarting the game.
3. If it does **not** enter the GSE flow, run the existing read-only `GameHours.AchievementProbe` for Big Walk and inspect the exact local source/layout.
4. Use that evidence to decide whether Big Walk is an already-supported source with a discovery gap or a genuinely missing emulator format. Do not broaden filesystem scans or add a parser without concrete evidence.

## Explicit non-goals

This batch does not:

- reconstruct historical unlocks that were never persisted;
- infer user unlock state from Steam percentages or schemas;
- require a Steam Web API key;
- depend on Hydra's backend;
- copy Hydra's Electron/Wine/souvenir/sync architecture;
- download icons into game directories;
- introduce polling or network activity into the achievement monitor;
- overwrite existing emulator metadata;
- claim Big Walk is fixed before its real local layout is verified.
