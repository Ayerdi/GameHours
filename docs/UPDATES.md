# Desktop updates

GameHours uses **Velopack 1.2.0** for Windows installation and self-updates. The Velopack NuGet package and the `vpk` CLI are intentionally pinned to the same version.

## Design goals

The updater does not own tracking policy. GameHours decides when it is safe to apply an update.

The desktop flow is now:

1. run the Velopack bootstrap directly from the packaged WPF entry point before normal WPF initialization;
2. check for an update silently after startup and then every six hours;
3. notify the user through the existing notification-area icon when a new version is found;
4. expose the installed version, channel, release notes and update actions in `Ajustes`;
5. download only after the user presses `Actualizar ahora`;
6. show download progress in the desktop UI;
7. persist the target release notes locally for the post-update `Novedades` view;
8. ask Velopack to wait for the current process to exit;
9. stop GameHours through the normal graceful tracker shutdown path;
10. let Velopack replace the installed files and restart the desktop app.

There is no forced update and no automatic download. A normal update check is read-only.

`GameHours.Core` contains only the `IAppUpdateService` contract and normalized `AppUpdate` model. The Velopack update-manager implementation remains isolated in `GameHours.Update`; the small lifecycle bootstrap is intentionally in the Desktop executable because Velopack requires it in the packaged main binary.

## Desktop experience

`Ajustes -> Actualizaciones` shows:

- installed version;
- Velopack channel (`Beta`, `Estable`, or development/unpackaged state);
- current update status;
- `Buscar actualizaciones`;
- `Ver novedades` when release notes are available;
- `Actualizar ahora` when a newer version is available;
- download progress while an update is being fetched.

When a silent check finds a new version, GameHours raises one tray notification for that version. Clicking a tray balloon opens the desktop app.

Release notes are shown inside GameHours rather than opening a browser. Markdown is rendered conservatively as readable plain text so the desktop does not need a web view or third-party Markdown renderer.

After an update, the release notes for the newly installed version are shown the first time the user opens the foreground desktop. If GameHours starts with `--background`, the `Novedades` window is deferred until the user opens GameHours. Notes remain available later from `Ajustes`.

The desktop stores only small update-presentation state in:

```text
%LOCALAPPDATA%\GameHours\update-state.json
```

This contains the most recently remembered release-note version/Markdown and whether that version's `Novedades` has been seen. It contains no credentials.

## Startup behavior

Velopack must process install/update hooks from the executable supplied to `vpk pack --mainExe`. To keep those hooks both verifiable and fast, GameHours follows Velopack's recommended WPF pattern:

1. `App.xaml` is compiled as a `Page` instead of generating WPF's implicit entry point;
2. `GameHours.Desktop.App` is the explicit `StartupObject`;
3. its `[STAThread] Main` runs:

```text
VelopackApp.Build()
    .SetAutoApplyOnStartup(false)
    .Run();
```

4. only after that returns does GameHours construct `App`, call `InitializeComponent()` and enter the WPF dispatcher.

This lets Velopack handle lifecycle arguments and exit without loading the normal WPF desktop. Auto-apply remains disabled, so a downloaded update cannot unexpectedly replace a running tracker. Applying an update goes through the same graceful shutdown path as an explicit user exit, ensuring an active measured session is finalized before process replacement.

The older `GameHours.App` development host retains its updater commands for diagnostics, but the packaged Windows application is `GameHours.Desktop.exe`.

## Update source

The desktop resolves its Velopack source in this order:

1. `GAMEHOURS_UPDATE_SOURCE` environment variable;
2. `update-source.txt` next to the installed desktop executable.

The source can be an HTTP(S) Velopack feed or a local/network release directory. An unpackaged `dotnet run` build deliberately reports that self-update is unavailable.

Production should use a read-only HTTPS endpoint owned by the GameHours deployment. Do **not** embed a GitHub personal access token in the desktop application merely to read releases from a private repository.

## Packaging GameHours Desktop

The repository pins the Velopack CLI in `.config/dotnet-tools.json` and includes `scripts/package-windows.ps1`.

The script publishes **`GameHours.Desktop`** as the package entry point and `GameHours.Desktop.exe` as the Velopack main executable. Packaging success is not reported until `scripts/validate-velopack-release.ps1` has verified the generated feed/index, full package and Setup executable and produced `SHA256SUMS.txt`.

Example beta package with release notes and a local test feed:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.2.0 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.2.0.md `
    -UpdateSource "C:\Users\Alex\GameHours\artifacts\velopack\beta"
```

For a production build, `-UpdateSource` should be the HTTPS feed URL instead.

When supplied, the script writes the update source to `update-source.txt` inside the package. It also copies the supplied release notes to `release-notes.md` so the installed version can show its own `Novedades` even when the update feed is temporarily unavailable. The same Markdown file is supplied to Velopack as the release's update notes.

If `-UpdateSource` is omitted, the package remains valid but in-app self-update is disabled unless `GAMEHOURS_UPDATE_SOURCE` is set externally.

Do not delete the release directory between versions. Existing package/feed metadata allow Velopack to create delta updates for later releases.

Normal CI now also exercises this packaging path with a synthetic beta version, so a change that builds/tests but no longer produces a valid Velopack release fails the normal gate.

## Local desktop smoke test

1. Package version `0.2.0` on `beta` with `-UpdateSource` pointing at the persistent local beta release directory.
2. Run the generated Setup executable and install GameHours Desktop.
3. Confirm `Ajustes -> Actualizaciones` reports installed `0.2.0` and channel `Beta`.
4. Keep `artifacts\velopack\beta` intact.
5. Package `0.2.1` into the same release directory with distinct release notes.
6. Start the installed desktop and confirm it reports `0.2.1` as available and raises one tray notification.
7. Open `Ver novedades` and confirm the `0.2.1` notes are shown.
8. Press `Actualizar ahora` and confirm progress reaches 100%.
9. If a game is active, confirm GameHours finalizes that measured session before exiting.
10. Confirm Velopack replaces the package, restarts GameHours Desktop and the existing SQLite database remains intact.
11. On first foreground open of `0.2.1`, confirm its `Novedades` is shown once and remains manually accessible from Settings afterwards.

These installed-machine checks are tracked centrally in `REAL-MACHINE-VALIDATION.md` and may remain pending while implementation continues.

## Previous real-machine validation

The underlying Velopack mechanism was already validated end to end on a real Windows host using the earlier packaged development host:

- `0.1.0` installed under `%LOCALAPPDATA%\Ayerdi.GameHours`;
- the installed build continued to use `%LOCALAPPDATA%\GameHours\gamehours.db`;
- packaging `0.1.1` generated a `0.1.0 -> 0.1.1` delta;
- update check reported installed `0.1.0`, beta channel and available `0.1.1`;
- update download handed off to Velopack after graceful process exit;
- the restarted/current installation reported version `0.1.1` and up-to-date state;
- the existing database and remembered games remained intact.

That validates the core installer/update mechanism. The current WPF update card, tray notification, bundled release notes and Desktop-as-main-executable path are implemented and package-validated in CI but remain pending installed-machine validation.

## Channels

Use at least:

- `beta` for development/test installations;
- `stable` for normal users.

The installed package remembers its Velopack channel. The update service therefore does not hard-code a channel in the client.

## Persistent data

The tracker database remains outside the installed application files at:

```text
%LOCALAPPDATA%\GameHours\gamehours.db
```

The Velopack package id is `Ayerdi.GameHours`, so its install root is separate from the tracker data directory. Application updates must never package, replace or delete the SQLite database.

## Before public distribution

The remaining production work includes:

- select a read-only HTTPS production update origin;
- extend the release workflow with Velopack remote download/upload once that host is selected;
- Windows code signing for the executable, updater and installer;
- final stable/beta policy;
- execute the deferred installed-machine checklist.

See also [`DISTRIBUTION.md`](DISTRIBUTION.md) and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md).
