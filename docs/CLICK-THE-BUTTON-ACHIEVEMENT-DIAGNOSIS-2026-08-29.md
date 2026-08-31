# Click the Button local-achievement diagnosis — 2026-08-29

This record captures the real-Windows investigation of `Click the Button` after automated GSE/Goldberg support existed but the GameHours detail view still reported no compatible local achievement source.

## Environment and identity

GameHours remembered:

```text
Title: Click the Button
Executable: D:\Games\Click.the.Button.v1.0.ZeiGames.com\Click the Button.exe
Steam-compatible AppID: 3946950
```

The first `GameHours.AchievementProbe` run incorrectly resolved the game root to `D:\Games` and consequently scanned sibling game directories. It selected Barony's AppID `371970` before later finding the target game's actual `steam_settings\steam_appid.txt` (`3946950`). This was a diagnostic-probe bug, not evidence that the runtime GSE reader used the wrong AppID.

The probe now starts at the executable directory and has a regression test with a `Click the Button` target and a sibling Barony installation. It must not scan the sibling or select its AppID.

## Real GSE configuration

The target installation contains `steam_settings` and these GSE configuration files:

```text
configs.app.ini
configs.main.ini
configs.user.ini
steam_appid.txt
steam_interfaces.txt
controller\...
```

The relevant `configs.user.ini` values are:

```ini
[user::saves]
local_save_path=./path/relative/to/dll
saves_folder_name=GSE Saves
```

No `GseSavePath` process, user or machine environment variable was set.

The configured portable path resolves under the game directory to:

```text
D:\Games\Click.the.Button.v1.0.ZeiGames.com\path\relative\to\dll
```

That directory does **not** exist on the tested machine.

## Achievement files actually present

A recursive read-only inspection found:

- no `steam_settings\achievements.json` catalogue;
- no other `achievements.json` anywhere under the game directory;
- no `%APPDATA%\GSE Saves\3946950` directory;
- no `%APPDATA%\Goldberg SteamEmu Saves\3946950` directory;
- no persisted GSE runtime achievement state at the configured portable path.

Therefore this specific installation provides GameHours with neither of the two local inputs normally available from GSE/Goldberg:

```text
steam_settings\achievements.json
    -> achievement definitions/catalogue

<resolved save root>\3946950\achievements.json
    -> user runtime unlock state
```

The GSE implementation itself loads the local definition catalogue separately from the persisted user state. Its normal achievement-setting path resolves a definition before writing user unlock state. The absence of both files is therefore not something GameHours can repair by searching additional unrelated directories.

## Decision

Do **not** broaden filesystem scanning, invent unlocked achievements, or treat this installation as a valid empty achievement snapshot.

The achievement provider continues to return `null` when no readable achievement source exists. That preserves the existing persistence/notification semantics and avoids establishing a false baseline.

GameHours now has a narrow presentation-only diagnostic for this case:

```text
GSE/Goldberg detectado · sin datos de logros
```

with the explanation:

```text
Esta instalación usa GSE/Goldberg, pero no incluye un catálogo de logros ni ha creado un estado local de desbloqueos. GameHours no puede mostrar logros que el emulador no haya almacenado.
```

The inspector activates only when GSE-style `configs.*.ini` files are present and neither a local catalogue nor a resolvable runtime state exists. Generic `steam_settings` directories are not classified as incomplete GSE support.

## Automated coverage

Focused automated coverage now verifies:

- the local achievement probe remains inside the target game and does not scan sibling installations;
- a GSE installation matching the observed `Click the Button` layout receives the specific missing-data diagnostic;
- a GSE installation with a catalogue does not receive that diagnostic;
- a generic `steam_settings` directory is not misidentified as GSE;
- the existing portable GSE reader still resolves `local_save_path` and can read runtime state with or without a local catalogue when such state actually exists.

## Remaining real-Windows validation

The final desktop build still needs one short visual check on the real machine:

1. open `Click the Button` in GameHours;
2. confirm the detail view shows the GSE-specific missing-data explanation instead of `Sin fuente local compatible`;
3. confirm normal library/tracking behavior is unaffected.

This validation proves the UX diagnosis. It does **not** claim that GameHours can recover achievements from this particular repack, because the required local source data is absent.
