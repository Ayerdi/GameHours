# GSE/Goldberg achievement provisioning — 2026-08-29

## Status

`AUTOMATED_VERIFIED` / `MANUAL_VALIDATION_REQUIRED`.

The final reviewed **functional** HEAD before real-machine validation is `53ae069d4fee3bcb6fe4d265c99f90ad9733115d`. PR CI #787 (`33274533516`) completed successfully. Because the workflow is triggered by `pull_request`, GitHub checked out and executed generated merge ref `53f7929891b9c2b09560aeb278606fb77852816f`, which merges that functional head with the current `main`. The run completed locked restore, Release build with 0 warnings / 0 errors, 130/130 Core tests + 167/167 Windows tests = 297/297, and self-contained `win-x64` Desktop publish smoke.

The branch may contain later documentation-only commits; those do not change the functional evidence above and must still pass normal PR CI before being treated as the current branch baseline.

A prior run, CI #782, correctly rejected the first explicit-confirmation implementation because `MessageBox` was ambiguous between WPF and WinForms. The fix explicitly uses `System.Windows.MessageBox`; the red run is retained as useful validation evidence rather than hidden.

Real-machine validation of the new write/launch lifecycle is still required before this batch can be marked `VERIFIED`.

This batch was explicitly authorized after two real games failed to expose local achievements through GameHours: `Click the Button` and `Big Walk`.

## Evidence and root cause

The earlier `Click the Button` investigation was correct about the local evidence that existed at the time: the installation had GSE/Goldberg configuration and Steam AppID `3946950`, but neither a local `steam_settings/achievements.json` catalogue nor persisted user unlock state. Broader filesystem searching could not recover data that had never been written.

Research of current Hydra Launcher and current GSE/Goldberg source clarified the missing lifecycle step:

- Hydra PR `hydralauncher/hydra#2697` generates missing emulator achievement metadata because Steam emulators only persist unlocks for achievements declared in `steam_settings/achievements.json`.
- GSE's achievement implementation loads its achievement database from the settings catalogue and resolves unlock calls by achievement API name. Missing icon metadata does not prevent the achievement from being defined or unlocked.
- GSE's current implementation reads the `hidden` display attribute as a string, and its shipped example catalogue uses `"hidden": "0"` / `"1"`.
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

The generated entries intentionally contain only the metadata required for correct achievement identity and compatible display attributes:

- `name`;
- `displayName` initially equal to the API name;
- empty `description`;
- `hidden = "0"` as required by GSE's string-based display attribute handling.

GameHours does not download achievement images into the game installation. GSE treats missing icon files as unavailable image handles, while GameHours' own metadata/artwork layer remains responsible for richer presentation.

Writing is fail-safe:

- existing `achievements.json` is never overwritten;
- content is written to a uniquely named temporary file with `FileMode.CreateNew`;
- the temporary file is moved into place only after serialization completes;
- a race where another process creates the destination is treated as `AlreadyPresent`;
- temporary files are cleaned up best-effort;
- the provisioner never writes user achievement state;
- an already-existing portable GSE runtime-state file is preserved byte-for-byte while the missing catalogue is provisioned.

### UX and mutation boundary

Opening or navigating to a game detail remains read-only. Detection of a missing GSE catalogue **does not** start a network request or write into the game installation.

When the detail view sees a GSE/Goldberg installation with neither catalogue nor runtime state, it:

1. explains the missing catalogue/state and tells the user that `Actualizar logros` can prepare future support;
2. waits for that explicit button click;
3. presents a WPF confirmation dialog before any write, naming `steam_settings\achievements.json`, explaining that only public Steam achievement identifiers are used, that user unlock state is not modified and that historical unlocks cannot be recreated;
4. performs discovery/network/file work away from the WPF UI thread only after confirmation;
5. reloads the local achievement provider if provisioning succeeds;
6. explains that GSE/Goldberg will consume the new definitions from the next game start;
7. keeps failure states non-destructive and retryable where appropriate.

Cancelling the confirmation performs no provisioning write. The achievement provider and active monitor themselves remain local-only; no network request was inserted into their periodic observation path.

## Automated coverage added

Focused tests cover:

- the real `Click the Button`-style GSE layout with AppID `3946950`;
- minimal catalogue generation without fabricating unlocked state;
- the exact GSE-compatible `"hidden": "0"` string format;
- case-insensitive deduplication of Steam API names;
- no icon requirement in the generated GSE file;
- no overwrite and no remote fetch when a catalogue already exists;
- preservation of an existing portable GSE runtime-state file while adding a previously missing catalogue;
- correct merge of that preserved partial state into the new complete catalogue;
- refusal to modify a generic `steam_settings` folder that is not recognized as GSE/Goldberg;
- `coldclient/steam_settings` discovery;
- sibling-game isolation;
- refusal to create an empty definitions file when the public catalogue is unavailable;
- parsing of Valve's global-achievement response shape, including a `0.0` percentage entry;
- recognition of the classic `steam_interfaces.txt` Goldberg marker.

CI #787 also executes the pre-existing achievement reader/runtime state, notification baseline, source locator, WPF resource and broader regression suites. The user-confirmation interaction itself still requires real WPF validation.

## Real-Windows validation required

### Click the Button

The earlier manual experiment deliberately created `D:\Games\Click.the.Button.v1.0.ZeiGames.com\steam_settings\achievements.json`; that file did **not** exist in the original repack. To test the new missing-catalogue UI itself, either use a fresh copy of the game or remove only that experiment-generated catalogue first. Do not delete a runtime-state file if GSE has subsequently created one.

1. Update/build the final reviewed branch head.
2. Restore the original missing-catalogue condition described above.
3. Open `Click the Button` in GameHours and confirm that merely opening the detail does not create `steam_settings\achievements.json`.
4. Confirm the UI explains the GSE/Goldberg missing-data case and offers the `Actualizar logros` preparation path.
5. Click `Actualizar logros`, cancel once and verify that no catalogue is created.
6. Click it again, accept the confirmation and verify that `steam_settings\achievements.json` is created inside the target game, not a sibling directory.
7. Confirm GameHours reloads a non-empty catalogue and reports that the next game start is required.
8. Close the game if it was already running, then start it again so GSE loads the new definitions during process initialization.
9. Earn a **new** achievement if a convenient one remains.
10. Confirm GSE creates/updates local runtime state and GameHours detects that unlock.
11. Do not expect achievements earned before the catalogue existed to appear unless the game/emulator itself later writes them.

### Big Walk

1. Open its detail after updating to the final branch head.
2. If GameHours detects GSE/Goldberg, confirm that opening the detail remains read-only, then exercise the same explicit confirmation flow and a fresh-unlock test after restarting the game.
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
- mutate a game installation merely because its detail view was opened;
- overwrite existing emulator metadata;
- claim Big Walk is fixed before its real local layout is verified.