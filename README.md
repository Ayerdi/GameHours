# GameHours

GameHours is a local-first Windows playtime engine designed to discover games, recover useful historical playtime and measure future sessions even when a game is not launched through Steam or another supported launcher.

The long-term product is the desktop companion for **Gestor de Juegos**. The tracking engine remains deliberately independent so it can work offline and could later be reused by a standalone client.

## Current status

The repository now contains a working local foundation plus the first live-tracking/discovery/update/desktop slices:

- SQLite persistence and immutable `tracking_started_at` cutover;
- timeline rules that prevent SRUM baseline/gap evidence from double-counting measured sessions;
- installed-game discovery for Steam, Epic and GOG;
- launcher-independent runtime detection for high-confidence Unreal and Unity executables;
- learned exact executable mappings stored locally;
- manual confirmation for unknown executables;
- Windows process monitoring with process-exit events plus permanent one-second reconciliation;
- a session engine that keeps one game session active until its last primary process exits;
- five-second durable checkpoints for active sessions;
- conservative interrupted-session recovery without assuming tracker downtime was playtime;
- graceful in-process shutdown that finalizes active sessions before exit;
- Windows suspend/resume detection that excludes sleeping time from measured playtime;
- local session persistence with `High` confidence for reconciliation-based boundaries;
- isolated Velopack-based Windows installer/self-update foundation with beta/stable packaging support;
- read-only SRUM inspection plus normalized/idempotent historical baseline import;
- an initial WPF desktop shell with tray behavior, live tracker status, local playtime library, graceful Exit and per-user Windows autostart.

Real-machine testing confirmed automatic loose-game detection and exact-path learning for Gothic 1 Remake, manual mapping/tracking for Project P.I.T.T., canonical multiprocess tracking, checkpoint-based interrupted-session recovery, idempotent SRUM baseline import, explicit graceful tracker shutdown, suspend/resume segmentation, and a packaged beta `0.1.0 -> 0.1.1` self-update including a generated delta and preserved local SQLite state.

## Architecture

```text
GameHours.Desktop      WPF desktop/tray host
GameHours.App          development and diagnostic CLI host
      |
      +-- GameHours.Core       domain, discovery/update contracts, session engine, timeline rules
      +-- GameHours.Windows    launcher discovery, process monitor, runtime resolver, SRUM reader
      +-- GameHours.Storage    local SQLite persistence
      +-- GameHours.Sync       optional Gestor de Juegos sync boundary
      +-- GameHours.Update     Velopack installer/update implementation
```

The key rule is **local-first**: tracking and historical reconstruction must keep working without an Internet connection or backend availability.

## Game discovery

Detection is layered rather than launcher-dependent:

1. Steam manifests and library folders;
2. Epic `.item` manifests;
3. GOG registry entries;
4. exact executable mappings learned locally;
5. conservative runtime signatures for loose Unreal/Unity games;
6. explicit user confirmation for otherwise unknown executables.

Loose runtime discoveries with the same remembered title are canonicalized to one local game identity so multiple executables do not become overlapping independent sessions. See [`docs/GAME-DISCOVERY.md`](docs/GAME-DISCOVERY.md).

## Playtime model

GameHours never blindly adds all available counters together.

```text
past                               tracking_started_at                         future
------------------------------------------|------------------------------------------>
        SRUM baseline (~estimated)        |     GameHours sessions (high/exact)
                                          |          [gap]
                                          |            + SRUM gap recovery (~estimated)
```

- `baseline` historical evidence must end at or before the tracker cutover;
- after the cutover, tracker sessions are authoritative for covered intervals;
- SRUM may only be used again for a verified uncovered gap;
- historical evidence that overlaps a measured session must not be counted again.

### Interrupted tracker runs

While a game is active, GameHours stores a local checkpoint every five seconds. On the next start, any interrupted session is finalized only through its last confirmed checkpoint. Time while GameHours was not observing the machine is deliberately left as a gap instead of being guessed as playtime. If the game is still running, the startup snapshot begins a new measured segment.

An intentional desktop shutdown is different from a crash: the desktop host requests a graceful stop, waits for the active session to be persisted and its checkpoint removed, and only then exits.

Sleep/resume is also treated as an explicit timeline boundary. The Windows monitor compares biased uptime with `QueryUnbiasedInterruptTime`; a suspended interval closes the pre-sleep segment and, if the game still exists after resume, starts a new segment instead of counting the sleeping interval.

See [`docs/PLAYTIME-TIMELINE.md`](docs/PLAYTIME-TIMELINE.md).

## Development

Requirements:

- Windows 10/11 for Windows-specific collectors;
- .NET 8 SDK.

```powershell
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release
dotnet test GameHours.sln -c Release
```

There are currently no GitHub Actions workflows. Local build/test is the quality gate until CI is added deliberately.

### Run the desktop shell

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj
```

The first desktop slice uses the existing `%LOCALAPPDATA%\GameHours\gamehours.db` database and starts tracking automatically. Closing the window hides it to the notification area; the tray menu or the in-window **Salir de GameHours** action performs the graceful tracker shutdown before the process exits.

`--background` starts directly in the tray and is the argument used by the per-user Windows autostart setting:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj -- --background
```

### Scan detected games

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- scan
```

### Diagnose an unknown executable

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- diagnose
```

Only processes started after diagnostic mode begins are printed. Unknown processes include their local executable path and are not counted as playtime.

### Confirm an unknown executable as a game

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- map "C:\Games\ProjectPIIT.exe" "Project P.I.I.T."
```

The mapping is local-only. Future launches of that exact executable resolve with `learned_executable_path`.

### Track playtime locally from the CLI

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- track
```

The CLI remains a diagnostic host. Production desktop lifecycle should use the explicit desktop/tray Exit flow rather than relying on console control signals.

### Inspect Windows SRUM

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
```

The diagnostic reads the ESE schema at `%WINDIR%\System32\sru\SRUDB.dat` without opening or modifying the GameHours SQLite database. An alternate offline SRUM copy can be supplied as the second argument.

See [`docs/SRUM.md`](docs/SRUM.md).

## Installer and self-updates

GameHours has an isolated Velopack 1.2.0 update implementation. `GameHours.Core` owns only the update contract; `GameHours.Update` owns Velopack-specific behavior.

Create a local beta installer/feed:

```powershell
.\scripts\package-windows.ps1 -Version 0.1.0 -Channel beta
```

The current package script still packages the CLI host while the WPF shell is being integrated. Moving the package entry point to `GameHours.Desktop`, then wiring graphical update notifications into the same graceful-shutdown lifecycle, is a follow-up desktop slice.

A real Windows smoke test validated install, beta channel detection, delta generation, download, graceful updater handoff, `0.1.0 -> 0.1.1` replacement/restart and persistence of the existing GameHours database.

See [`docs/UPDATES.md`](docs/UPDATES.md).

## Next vertical slices

1. validate the first WPF desktop/tray shell on a real Windows host;
2. graphical unresolved-candidate/executable-role confirmation UI;
3. move Velopack packaging/update coordination to `GameHours.Desktop`;
4. synchronize one real measured session end-to-end with Gestor de Juegos;
5. add desktop authentication/sync status and update settings;
6. production update hosting, CI and Windows code signing.

## Privacy direction

Raw SRUM databases, registry data, PIDs and full machine paths are local implementation details. The backend integration should receive only the minimum normalized information needed to associate playtime with a game and account.
