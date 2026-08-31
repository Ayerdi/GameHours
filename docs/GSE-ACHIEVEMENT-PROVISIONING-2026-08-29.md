# GSE/Goldberg achievement provisioning — 2026-08-29

## Status

`AUTOMATED_VERIFIED` / `PROVISIONING_WRITE_PATH_MANUALLY_VERIFIED` / `FRESH_UNLOCK_AND_ARTWORK_MANUAL_VALIDATION_REQUIRED`.

The latest reviewed **functional** HEAD is `fec9f56e0be42d73eee0bd9a76d664772db4f94c`. PR CI #802 (`33284727156`) completed successfully on generated merge ref `ee4b603d3425eedf8f091b681be5fab523896e08`, which merges that head with current `main`. The run completed locked restore, Release build with **0 warnings / 0 errors**, 132/132 Core tests + 173/173 Windows tests = **305/305**, and self-contained `win-x64` Desktop publish smoke.

The real `Click the Button` installation has now validated the explicit-confirmation provisioning write path. A fresh post-fix achievement unlock and the new asynchronous artwork presentation still require real-Windows validation before this whole achievement path can be marked fully `VERIFIED`. `Big Walk` remains a separate real-layout validation case.

## Evidence and root cause

The earlier `Click the Button` investigation was correct about the local evidence that existed at the time: the installation had GSE/Goldberg configuration and Steam AppID `3946950`, but originally lacked `steam_settings/achievements.json`. Broader filesystem searching could not reconstruct metadata or historical unlock state that the emulator had not persisted.

Research of current Hydra Launcher and current GSE/Goldberg source clarified the missing lifecycle step:

- Hydra PR `hydralauncher/hydra#2697` generates missing emulator achievement metadata because Steam emulators only persist unlocks for achievements declared in `steam_settings/achievements.json`.
- GSE's achievement implementation loads its achievement database from the settings catalogue and resolves unlock calls by achievement API name. Missing icon metadata does not prevent the achievement from being defined or unlocked.
- GSE's current implementation reads the `hidden` display attribute as a string, and its shipped example catalogue uses `"hidden": "0"` / `"1"`.
- Valve documents `ISteamUserStats/GetGlobalAchievementPercentagesForApp/v2` with only `gameid` as a required argument. It exposes public global achievement API names without requiring a user or publisher Web API key.
- Valve documents `GetSchemaForGame` as requiring an API key, so GameHours does not make that endpoint a client-side dependency.
- Valve's `GetAchievementAndUnlockTime` contract explicitly separates unlock state from unlock-time availability. That reinforces the product rule that GameHours must not present an old GSE timestamp as an exact historical unlock time unless it has enough evidence to trust it.

The product conclusions are:

1. local user state remains authoritative for whether an achievement is unlocked;
2. a remote catalogue may supply definitions/presentation metadata only;
3. GameHours may safely seed a missing GSE catalogue so the emulator can record future unlocks;
4. catalogue data must never be interpreted as user unlock state;
5. historical unlocks that GSE never persisted cannot be reconstructed from the catalogue;
6. raw emulator timestamps are preserved, but first-observation GSE timestamps are presented conservatively when GameHours cannot verify that they are the original historical unlock times.

## Manual evidence — Click the Button, 2026-08-30

The real installation at `D:\Games\Click.the.Button.v1.0.ZeiGames.com` produced the following evidence:

- after restoring the missing-catalogue condition, merely running GameHours did not create `steam_settings\achievements.json`;
- accepting the explicit `Preparar logros GSE/Goldberg` confirmation created exactly one catalogue at `D:\Games\Click.the.Button.v1.0.ZeiGames.com\steam_settings\achievements.json`;
- the generated catalogue contained **15 definitions**;
- the existing portable GSE runtime state remained present at `path\relative\to\dll\3946950\achievements.json`;
- the runtime-state SHA-256 remained byte-for-byte identical before and after provisioning: `905147EF2DC35956D42A936A8116B294C65AAA229E5FC7D28F38B67993074AC4`;
- GameHours displayed **14/15**, which the user confirmed matches the real unlock state rather than a discovery failure;
- the Steam metadata cache for AppID `3946950` contains localized names/descriptions plus valid `iconUrl` and `lockedIconUrl` values under the official `cdn.steamstatic.com/steamcommunity/public/images/apps/...` path.

That last point isolated the blank-artwork defect below the metadata layer: GameHours already had the correct artwork references, but the old WPF remote-`BitmapImage` path was not reliably producing visible images.

The same manual session also showed all historical GSE achievements with the same displayed time. Because those achievements predated GameHours' observation, that timestamp cannot be proven to be each achievement's original unlock time. The fix therefore changes **presentation confidence**, not the raw source evidence.

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

Both the GSE catalogue reader and modern GSE runtime-state locator reuse this discovery.

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

The richer Steam metadata cache remains presentation-only and optional; it is not required for GSE to persist unlocks.

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

GameHours does not download achievement images into the game installation.

Writing is fail-safe:

- existing `achievements.json` is never overwritten;
- content is written to a uniquely named temporary file with `FileMode.CreateNew`;
- the temporary file is moved into place only after serialization completes;
- a race where another process creates the destination is treated as `AlreadyPresent`;
- temporary files are cleaned up best-effort;
- the provisioner never writes user achievement state;
- an already-existing portable GSE runtime-state file is preserved byte-for-byte while the missing catalogue is provisioned.

### Historical unlock-time confidence

GameHours now distinguishes a source-provided timestamp from a timestamp that it can safely present as historical fact.

For persisted GSE/Goldberg achievements, a row is treated as **historical time unverified** when the achievement was already unlocked the first time GameHours ever saw it (`first_unlocked_seen_at_utc == first_seen_at_utc`). This is intentionally source-aware: the rule does not downgrade first-observation timestamps from official Steam sources.

Important properties of the fix:

- the raw `unlocked_at_utc` supplied by GSE is **not deleted or rewritten**;
- the UI no longer presents that baseline GSE timestamp as an exact historical unlock time;
- affected rows show `Desbloqueado · hora histórica no disponible`;
- first-achievement summaries avoid fabricating a historical date when older GSE unlocks are unverified;
- activity/calendar read models use the time GameHours first observed the baseline unlock and mark it as an approximate/observed-time event;
- a later achievement that GameHours previously observed locked and then sees become unlocked retains its GSE source timestamp as the best available exact time.

This is a read/presentation confidence rule rather than a destructive migration, so future improvements can still inspect the original emulator evidence.

### Achievement artwork cache and asynchronous WPF presentation

The old path relied on WPF loading a remote `BitmapImage` directly from the Steam CDN. The metadata test on the real game proved the URLs were already correct, so the fix is localized to presentation rather than changing Steam metadata discovery.

`SteamAchievementArtworkCache` now:

- accepts only HTTPS URLs on the exact `cdn.steamstatic.com` host and expected Steam achievement-image path;
- rejects non-default ports, query-bearing URLs and path shapes outside one `<appid>/<filename>` asset;
- disables automatic redirects on the production HTTP client;
- reuses one long-lived `HttpClient`;
- limits active artwork downloads to four at a time;
- applies an 8-second request timeout and a 2 MiB per-file ceiling;
- writes to `%LOCALAPPDATA%\GameHours\cache\steam-achievement-images\<appid>\<filename>` through a temporary file followed by move-into-place;
- deduplicates concurrent requests for the same asset;
- never writes artwork into the game installation.

`LocalAchievementImageService` now performs synchronous work only for local/cached files. A missing Steam image is fetched asynchronously, decoded locally with `BitmapCacheOption.OnLoad`, frozen for safe reuse and then propagated through `INotifyPropertyChanged` so an achievement row can update without blocking the WPF UI thread.

The Steam asset filenames are content-addressed hashes in the tested metadata, so the local image cache does not need a periodic refresh path for the same URL.

### UX and mutation boundary

Opening or navigating to a game detail remains read-only with respect to the game installation. Detection of a missing GSE catalogue does not start provisioning or write into the game installation.

When the detail view sees a GSE/Goldberg installation with missing support, it:

1. explains the missing catalogue/state and tells the user that `Actualizar logros` can prepare future support;
2. waits for that explicit button click;
3. presents a WPF confirmation dialog before any provisioning write;
4. performs discovery/network/file work away from the WPF UI thread only after confirmation;
5. reloads the local achievement provider if provisioning succeeds;
6. explains that GSE/Goldberg will consume new definitions from the next game start;
7. keeps failures non-destructive and retryable where appropriate.

Artwork downloads are a separate presentation-only cache under GameHours' own local-data directory. They do not mutate the game and are not part of the periodic achievement monitor.

## Automated coverage

Focused coverage includes:

- the real `Click the Button`-style GSE layout with AppID `3946950`;
- minimal catalogue generation without fabricating unlocked state;
- exact GSE-compatible `"hidden": "0"` format;
- no overwrite/no remote fetch when a catalogue already exists;
- preservation of an existing portable GSE runtime-state file while adding a missing catalogue;
- correct merge of preserved partial state into the new complete catalogue;
- refusal to modify generic/non-GSE `steam_settings` layouts;
- `coldclient/steam_settings` discovery and sibling-game isolation;
- refusal to create an empty definitions file when the public catalogue is unavailable;
- legacy GSE baseline timestamps being read as historical-time-unverified without deleting the raw source value;
- later GSE locked→unlocked transitions retaining their source timestamp;
- row presentation for unverified historical timestamps;
- trusted Steam artwork downloading once and reusing the disk cache;
- rejection of untrusted artwork URLs without a network request;
- rejection of oversized artwork without a cache write.

CI #802 (`33284727156`) validates the final functional head described above: locked restore, Release build with 0 warnings / 0 errors, **305/305 tests**, and self-contained `win-x64` Desktop publish.

## Remaining real-Windows validation

### Click the Button

Provisioning steps are now validated. The remaining focused gate is:

1. update to functional head `fec9f56e…` or a later documentation-only head containing it;
2. open Click the Button's detail while online and confirm achievement artwork populates without freezing the UI;
3. confirm the existing 14 baseline achievements no longer claim the repeated historical `17:52` time as exact;
4. confirm GameHours still reports the real **14/15** state;
5. close and restart the game so GSE has loaded the generated catalogue from process initialization;
6. earn the remaining achievement;
7. confirm GSE updates local runtime state and GameHours changes to **15/15**;
8. if GSE writes an unlock timestamp for that locked→unlocked transition, confirm GameHours displays that new timestamp while leaving the 14 historical baseline times unverified.

### Big Walk

1. Open its detail after updating to the current branch.
2. If GameHours detects GSE/Goldberg, exercise the same explicit-confirmation/fresh-unlock path.
3. If it does not enter the GSE flow, run the existing read-only `GameHours.AchievementProbe` and inspect the exact local source/layout.
4. Add support only from concrete evidence; do not broaden filesystem scans or add a parser speculatively.

## Explicit non-goals

This work does not:

- reconstruct historical unlocks or exact historical times that cannot be verified;
- delete raw source timestamps merely because their presentation confidence is lower;
- infer user unlock state from Steam percentages or schemas;
- require a Steam Web API key;
- depend on Hydra's backend;
- copy Hydra's Electron/Wine/souvenir/sync architecture;
- download icons into game directories;
- introduce polling or artwork network activity into the achievement monitor;
- mutate a game installation merely because its detail view was opened;
- overwrite existing emulator metadata;
- claim Big Walk is fixed before its real local layout is verified.
