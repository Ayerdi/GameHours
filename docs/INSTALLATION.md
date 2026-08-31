# Installing and removing GameHours on Windows

GameHours is a local-first Windows application. Installation files and user data deliberately live in different directories so an application update or reinstall does not replace play history.

## Supported platform

The current package target is:

- Windows 10/11;
- x64;
- self-contained .NET desktop package — end users do not need to install the .NET SDK.

A public download is not considered ready until the signed-release and installed-update gates in [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md) have passed.

## Installing a release

For a public release, use only the GameHours Setup produced by the repository's signed **Package Windows** workflow and published through the selected official distribution location.

The Velopack package id is:

```text
Ayerdi.GameHours
```

and its Windows application files are managed under the corresponding `%LOCALAPPDATA%` install root. Do not place user saves, backups or databases inside that directory.

After installation:

1. open GameHours normally from its installed shortcut;
2. verify your Library opens and tracking starts normally;
3. use **Ajustes → Actualizaciones** to see the installed version/channel and future updates.

GameHours does not automatically download or force an update. Download/apply remains an explicit user action.

## Where GameHours stores your data

Persistent user data is separate from the Velopack application directory:

```text
%LOCALAPPDATA%\GameHours\
```

The primary database is:

```text
%LOCALAPPDATA%\GameHours\gamehours.db
```

That directory can also contain preferences, update-presentation state, diagnostics and backups. Machine-specific paths and local tracking metadata stay local to the PC unless an explicit export/integration says otherwise.

## Backing up before recovery or migration

For a consistent backup, prefer **Ajustes → Crear copia de seguridad…**. GameHours uses SQLite's backup API and validates the snapshot rather than copying a live WAL database blindly.

A backup is strongly recommended before any manual recovery, OS migration or deliberate full-data deletion.

## Updating GameHours

Use **Ajustes → Actualizaciones**:

1. `Buscar actualizaciones` performs a read-only check;
2. `Ver novedades` shows the target release notes when available;
3. `Actualizar ahora` downloads only after explicit confirmation;
4. GameHours gracefully finalizes active tracking before handing off replacement to Velopack;
5. Velopack restarts the installed application on the new version.

User data under `%LOCALAPPDATA%\GameHours` must remain untouched by the application package. CI also rejects `gamehours.db` and private signing material if either is ever found inside a generated package.

## Uninstalling the application

Remove GameHours through normal Windows **Installed apps / Apps & features**.

Velopack removes the `Ayerdi.GameHours` application install directory and its shortcuts. GameHours intentionally stores its user database outside that install root, so uninstalling/reinstalling the application is designed to preserve your local play history.

This preservation behavior still has an explicit installed-machine validation gate before public release.

## Completely deleting GameHours data

Uninstalling the application is **not** the same as deleting your play history.

If you intentionally want a complete local-data removal:

1. create/export any backup you want to keep;
2. uninstall GameHours from Windows;
3. close any remaining GameHours process;
4. delete only this data directory:

```text
%LOCALAPPDATA%\GameHours
```

That step is destructive. Do not delete the directory merely to repair/reinstall the application.

## Recovery after a bad update

GameHours does not enable routine automatic downgrades. The normal production recovery path is a higher-version signed hotfix.

If a broken installation cannot start, preserve `%LOCALAPPDATA%\GameHours` (and create a database backup when possible), then use a known-good signed Setup through the controlled recovery procedure. The application binaries may be replaced; the user-data directory should not be removed.

See [`UPDATES.md`](UPDATES.md) for the update/recovery policy.

## Local developer/test packages

Unsigned local packages are development artifacts, not public releases. Windows may identify them as an unknown publisher.

A local Velopack update test uses `GAMEHOURS_UPDATE_SOURCE` as an explicit runtime override to a persistent local feed. A local filesystem path must not be embedded into a distributed package's `update-source.txt`.

See [`UPDATES.md`](UPDATES.md) for the exact current test contract.
