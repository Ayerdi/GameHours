# Desktop updates

GameHours uses **Velopack 1.2.0** for Windows installation and self-updates. The Velopack NuGet package and the `vpk` CLI are intentionally pinned to the same version.

## Design goals

The updater must not own tracking policy. GameHours decides when it is safe to apply an update.

The intended production flow is:

1. check for an update in the background;
2. notify the user in the desktop UI;
3. optionally download the update in the background;
4. wait until no game session is active, unless the user explicitly requests an immediate update;
5. flush/checkpoint local tracking state;
6. ask Velopack to wait for GameHours to exit gracefully;
7. exit GameHours;
8. Velopack replaces the installed files and restarts GameHours.

`GameHours.Core` only contains the `IAppUpdateService` contract and normalized `AppUpdate` model. The Velopack implementation lives in `GameHours.Update` so tracking, storage, discovery and sync do not depend on Velopack.

## Startup behavior

`VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` is executed before normal GameHours initialization.

Auto-apply is disabled deliberately. A downloaded update must not unexpectedly replace the tracker while a game is being measured. The future UI/tray layer will explicitly coordinate update application with active-session shutdown/checkpointing.

## Update source

The application accepts either an HTTP(S) Velopack feed or a local/network release directory. During development the source is passed explicitly:

```powershell
GameHours.App.exe update-check "C:\path\to\velopack\beta"
```

or through:

```powershell
$env:GAMEHOURS_UPDATE_SOURCE = "C:\path\to\velopack\beta"
```

Production should use a read-only HTTPS endpoint owned by the GameHours/Gestor deployment. Do **not** embed a GitHub personal access token in the desktop application merely to read releases from a private repository.

## Local packaging

The repository pins the Velopack CLI in `.config/dotnet-tools.json` and includes `scripts/package-windows.ps1`.

Create a beta package:

```powershell
.\scripts\package-windows.ps1 -Version 0.1.0 -Channel beta
```

The script:

- restores the pinned `vpk` tool;
- publishes `GameHours.App` as `win-x64` self-contained;
- stamps the requested application version;
- packages it as `Ayerdi.GameHours`;
- writes the installer/feed into `artifacts\velopack\beta`.

Do not delete the release directory between versions. Existing packages/feed metadata allow Velopack to create delta updates for later releases.

Optional Markdown release notes can be embedded:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.1.1 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.1.1.md
```

## Local end-to-end smoke test

1. Package `0.1.0` on channel `beta`.
2. Run the generated Setup executable and install GameHours.
3. Keep `artifacts\velopack\beta` intact.
4. Package `0.1.1` into the same beta release directory.
5. From the **installed** copy, check the local feed:

```powershell
& "$env:LOCALAPPDATA\Ayerdi.GameHours\current\GameHours.App.exe" `
    update-check "C:\Users\Alex\GameHours\artifacts\velopack\beta"
```

6. Apply it:

```powershell
& "$env:LOCALAPPDATA\Ayerdi.GameHours\current\GameHours.App.exe" `
    update-now "C:\Users\Alex\GameHours\artifacts\velopack\beta"
```

`update-now` checks, downloads and asks the Velopack updater to wait for GameHours to exit gracefully. The process then exits normally and Velopack restarts it with `scan`.

The development commands intentionally reject `dotnet run`/unpackaged builds because replacing compiler output in place is not a supported or safe self-update path.

## Channels

Use at least:

- `beta` for development/test installations;
- `stable` for normal users.

The installed package remembers its Velopack channel. The update service therefore does not hard-code a channel in the client.

## Persistent data

The current tracker database remains outside the installed application files at:

```text
%LOCALAPPDATA%\GameHours\gamehours.db
```

The Velopack package id is `Ayerdi.GameHours`, so its install root is separate from the tracker data directory. Application updates must never package, replace or delete the SQLite database.

## Before public distribution

The packaging pipeline still needs:

- an HTTPS production update origin;
- release automation/CI;
- Windows code signing for the executable, updater and installer;
- a graphical update notification/settings experience;
- a clean-shutdown coordinator that waits for active tracking state to be flushed before applying a downloaded update;
- stable/beta policy and release-note generation.
