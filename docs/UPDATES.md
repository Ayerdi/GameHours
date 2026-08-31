# Desktop updates

GameHours uses **Velopack 1.2.0** for Windows installation and self-updates. The Velopack NuGet package and the `vpk` CLI are intentionally pinned to the same version.

## Design goals

The updater does not own tracking policy. GameHours decides when it is safe to apply an update.

The desktop flow is:

1. run the Velopack bootstrap from the packaged WPF entry point before normal WPF initialization;
2. check for updates silently after startup and then every six hours;
3. notify through the existing tray icon when a new version is found;
4. expose installed version, channel, release notes and update actions in `Ajustes`;
5. download only after the user presses `Actualizar ahora`;
6. show download progress;
7. persist target release notes for the post-update `Novedades` view;
8. ask Velopack to wait for the current process to exit;
9. stop GameHours through the normal graceful tracker shutdown path;
10. let Velopack replace installed files and restart the desktop app.

There is no forced update and no automatic download. A normal update check is read-only.

`GameHours.Core` contains only the `IAppUpdateService` contract and normalized `AppUpdate` model. Velopack remains isolated in `GameHours.Update`; the lifecycle bootstrap is intentionally in the Desktop executable because Velopack requires it in the packaged main binary.

## Desktop experience

`Ajustes -> Actualizaciones` shows:

- installed version;
- Velopack channel (`Beta`, `Estable`, or development/unpackaged state);
- current update status;
- `Buscar actualizaciones`;
- `Ver novedades` when release notes are available;
- `Actualizar ahora` when a newer version is available;
- download progress while an update is being fetched.

A silent check raises at most one tray notification for a newly discovered version. Release notes are rendered inside GameHours without a web view or third-party Markdown renderer.

After an update, notes for the newly installed version are shown on the first foreground open. Background startup defers that window. Small presentation state lives in:

```text
%LOCALAPPDATA%\GameHours\update-state.json
```

It contains no credentials.

## Startup behavior

The packaged main executable is `GameHours.Desktop.exe`. Its explicit `[STAThread] Main` executes:

```text
VelopackApp.Build()
    .SetAutoApplyOnStartup(false)
    .Run();
```

before normal WPF initialization. This lets Velopack process install/update lifecycle arguments without loading the regular application. Auto-apply remains disabled so a downloaded update cannot unexpectedly replace a running tracker. Applying an update uses the same graceful shutdown path as an explicit exit, allowing active measured sessions to finalize first.

## Update-source policy

The Desktop resolves update configuration in this order:

1. explicit `GAMEHOURS_UPDATE_SOURCE` environment override;
2. typed bundled `update-source.json`;
3. legacy bundled `update-source.txt` for compatibility.

An invalid higher-priority source fails closed instead of silently falling through to a lower-priority source.

### Public GameHours packages

New public packages embed:

```json
{"type":"github","repository":"https://github.com/Ayerdi/GameHours"}
```

The Desktop constructs Velopack's `GithubSource` for that configuration. GitHub Releases is therefore treated as a release API/source, not as a static HTTP directory.

The repository is public, so installed clients require no GitHub token. The installed Velopack channel controls which feed asset is used; `beta` additionally permits GitHub prereleases while `stable` ignores them.

No GitHub PAT, deployment credential or signing secret is stored in `update-source.json` or compiled into the app.

### Explicit development/test override

`GAMEHOURS_UPDATE_SOURCE` may be:

- a fully-qualified local/network release directory; or
- a compatible HTTPS source.

HTTP and relative paths are rejected. This override exists primarily for local installed-update testing and controlled diagnostics.

The older bundled `update-source.txt` format accepts only an absolute HTTPS URL without credentials, query string or fragment and remains supported so older package configurations do not break.

## Packaging GameHours Desktop

The repository pins the Velopack CLI in `.config/dotnet-tools.json`. `scripts/package-windows.ps1` publishes `GameHours.Desktop` self-contained for `win-x64`, then packages it through Velopack.

Packaging success requires `scripts/validate-velopack-release.ps1` to verify the feed/index, full package and Setup executable. The validator also:

- generates `SHA256SUMS.txt`;
- can require a delta package;
- checks signed release candidates with Authenticode;
- rejects user/signing material from the payload;
- verifies that the packaged update source exactly matches the source requested by the packaging command.

A local unsigned package does **not** need an embedded update source:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.2.0-beta.1 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.2.0-beta.1.md
```

A public package embeds the GitHub Releases source explicitly:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.2.0-beta.1 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.2.0-beta.1.md `
    -GithubUpdateRepository https://github.com/Ayerdi/GameHours
```

Public CI additionally supplies Azure Artifact Signing metadata.

Do not delete a local release directory between versions. Existing package/feed metadata allow Velopack to generate delta updates for later releases.

## Correct local update test

The local feed is intentionally supplied at runtime rather than embedded in the test package.

1. Keep one persistent local beta release directory.
2. Package `0.2.0-beta.1` into it without embedding a local source.
3. Set `GAMEHOURS_UPDATE_SOURCE` for the installed test process to the absolute path of that beta release directory.
4. Install and open `0.2.0-beta.1`.
5. Confirm `Ajustes -> Actualizaciones` reports `0.2.0-beta.1`, channel `Beta`, and can use the local feed.
6. Keep the release directory intact and package `0.2.0-beta.2` into the same directory with distinct release notes.
7. From the installed `beta.1`, run `Buscar actualizaciones` and confirm `beta.2` is offered.
8. Confirm `Ver novedades` shows the `beta.2` notes.
9. Press `Actualizar ahora` and confirm progress completes.
10. If a game is active, confirm the current measured session is finalized before GameHours exits.
11. Confirm Velopack restarts GameHours as `beta.2` and `%LOCALAPPDATA%\GameHours\gamehours.db` is unchanged and readable.
12. Confirm `Novedades` is shown once on the first foreground open and remains accessible from Settings.

This installed-machine path remains a manual validation gate.

## Public release flow

The manual `Package Windows` workflow follows the remote Velopack lifecycle rather than copying feed files manually:

```text
vpk download github
      -> package/sign/validate
      -> SHA256 + GitHub attestation
      -> Actions artifact copy
      -> vpk upload github --publish
```

For beta, download/upload includes GitHub prereleases. If a previous full package was downloaded, the new package is required to contain a delta. For the first release, no delta is required.

Release tags are immutable and use `v<SemVer>`. The workflow rejects a duplicate tag before packaging.

The workflow also enforces channel/version consistency:

- beta requires a prerelease SemVer such as `0.2.0-beta.1`;
- stable requires a non-prerelease SemVer such as `0.2.0`.

## Recovery policy

GameHours does **not** enable feed-driven version downgrades by default. A trusted update source should not gain routine permission to push clients backwards to an older potentially vulnerable build.

Normal recovery from a bad release is forward-only:

1. stop treating the bad release as the desired target;
2. fix the issue;
3. publish a higher-version signed hotfix through the same release path.

For an exceptional case where the installed application cannot start, recovery is a controlled reinstall of a known-good signed Setup rather than an automatic downgrade policy. Before an emergency reinstall, preserve a database backup when possible.

Velopack owns the application install root under `%LOCALAPPDATA%\Ayerdi.GameHours`, while GameHours user data lives separately under `%LOCALAPPDATA%\GameHours`. Updating or reinstalling application binaries must not package, replace or delete that data directory. The release validator explicitly rejects `gamehours.db` from package contents; real uninstall/reinstall preservation still requires installed-machine verification.

## Previous real-machine evidence

The underlying Velopack mechanism was previously exercised end to end on a real Windows host using the earlier packaged development host:

- `0.1.0` installed under `%LOCALAPPDATA%\Ayerdi.GameHours`;
- the installed build continued to use `%LOCALAPPDATA%\GameHours\gamehours.db`;
- packaging `0.1.1` generated a delta;
- update check reported installed `0.1.0`, beta channel and available `0.1.1`;
- update download handed off after graceful process exit;
- the restarted installation reported `0.1.1`;
- the existing database and remembered games remained intact.

That validates the underlying mechanism. The current WPF update card, tray notification, release notes, Desktop-as-main-executable path and hardened source policy still need their current installed-machine smoke.

## Code signing and provenance

The release workflow is prepared for **Azure Artifact Signing** through GitHub OIDC and Velopack's `--azureTrustedSignFile` integration. No PFX/private key is stored in the repository.

A signed release must pass Authenticode validation before hashes, GitHub artifact attestation and public upload. Authenticode identifies the Windows publisher; the GitHub attestation records build provenance. They are complementary controls.

The Azure account/profile and federated identity are external configuration prerequisites and remain unverified until the release workflow is run from `main`.

## Before public distribution

Remaining gates are:

- provision/validate Azure Artifact Signing + federated GitHub identity;
- validate the signed GitHub Releases workflow from `main`;
- execute clean install, in-app update and controlled recovery on a real Windows machine;
- evaluate SmartScreen with a signed binary.

See also [`DISTRIBUTION.md`](DISTRIBUTION.md), [`SUPPLY-CHAIN.md`](SUPPLY-CHAIN.md) and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md).
