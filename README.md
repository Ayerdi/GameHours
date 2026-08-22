# GameHours

GameHours is a local-first Windows playtime engine designed to discover games, recover useful historical playtime and measure future sessions even when a game is not launched through Steam or another supported launcher.

The long-term product is the desktop companion for **Gestor de Juegos**. The tracking engine remains deliberately independent so it can work offline and could later be reused by a standalone client.

## Current status

The repository now contains a working local foundation plus live tracking, historical recovery, achievements, desktop UI and self-update slices:

- SQLite persistence and immutable `tracking_started_at` cutover;
- timeline rules that prevent SRUM baseline/gap evidence from double-counting measured sessions;
- installed-game discovery for Steam, Epic and GOG;
- launcher-independent runtime detection for high-confidence Unreal and Unity executables;
- a layered Windows detection-evidence engine using GameConfigStore, runtime signatures, graphics/window observations and process relationships without treating any weak signal as authoritative by itself;
- conservative executable-role classification for primary/secondary game processes, launchers, anti-cheat, updaters, crash handlers and helpers;
- learned exact executable mappings stored locally, including conservative launcher/helper -> real game child-process learning;
- short-lived launcher relationship history that can recover an exact parent PID for up to 30 seconds after the parent exits, with process-start-time guards against PID reuse;
- manual confirmation for unknown executables;
- a graphical **Pendientes** candidate center that persists low-confidence executables, explains their evidence and lets the user create/associate a game, classify a helper role or ignore the executable once;
- durable local executable-role overrides that are reloaded by the detection engine without weakening the automatic tracking threshold;
- Windows process monitoring with process-exit events plus permanent one-second reconciliation;
- a session engine that keeps one game session active until its last trackable process exits;
- five-second durable checkpoints for active sessions;
- conservative interrupted-session recovery without assuming tracker downtime was playtime;
- graceful in-process shutdown that finalizes active sessions before exit;
- Windows suspend/resume detection that excludes sleeping time from measured playtime;
- local session persistence with `High` confidence for reconciliation-based boundaries;
- read-only SRUM inspection plus normalized/idempotent historical baseline import;
- local achievement discovery/parsing, normalized SQLite state and live session-scoped unlock monitoring;
- integrated **Calendario** and **Estadísticas** views in the main desktop navigation, backed by the existing local activity/statistics services and loaded only when opened;
- a WPF desktop shell with tray behavior, live tracker status, local playtime library, game detail, unified activity, graceful Exit and per-user Windows autostart;
- Velopack-based desktop installation/self-update with beta/stable packaging, release notes and an in-app update card;
- Windows GitHub Actions CI for restore, Release build and solution tests on `feat/desktop-foundation` and pull requests.

Real-machine testing confirmed automatic loose-game detection and exact-path learning for Gothic 1 Remake, manual mapping/tracking for Project P.I.T.T., canonical multiprocess tracking, checkpoint-based interrupted-session recovery, idempotent SRUM baseline import, explicit graceful tracker shutdown, suspend/resume segmentation, local GSE achievement parsing for Project P.I.T.T. and the underlying Velopack `0.1.0 -> 0.1.1` update mechanism with a generated delta and preserved SQLite state.

The newer multi-source achievement layer, packaged WPF update entry point, graphical update workflow, Windows detection evidence/process-family/grace signals, graphical candidate-center workflow and embedded Calendar/Statistics navigation remain pending real-machine validation. That validation is deliberately non-blocking while feature implementation continues.

## Architecture

```text
GameHours.Desktop      WPF desktop/tray host and packaged Windows entry point
GameHours.App          development and diagnostic CLI host
      |
      +-- GameHours.Core       domain, discovery/update contracts, session engine, timeline rules
      +-- GameHours.Windows    launcher discovery, process monitor, runtime resolver, SRUM/achievement readers
      +-- GameHours.Storage    local SQLite persistence
      +-- GameHours.Sync       optional Gestor de Juegos sync boundary
      +-- GameHours.Update     Velopack installer/update implementation
```

The key rule is **local-first**: tracking, historical reconstruction and compatible local achievement reads must keep working without an Internet connection or backend availability.

## Game discovery

Detection is layered rather than launcher-dependent:

1. Steam manifests and library folders;
2. Epic `.item` manifests;
3. GOG registry entries;
4. exact executable mappings learned locally;
5. conservative runtime signatures for loose Unreal/Unity games;
6. exact per-user Windows GameConfigStore evidence;
7. supporting graphics/window/process-relationship evidence, including a short exact-parent-PID history for launchers that exit early;
8. explicit user confirmation for otherwise unknown executables.

Weak evidence never starts tracking by itself. Direct3D/OpenGL/Vulkan usage plus a visible window is kept as a low-confidence candidate, while helper-like executable roles can veto automatic tracking. A graphical child process can be promoted to a known game only when its immediate parent has already been learned locally as that game's helper. If the parent exits first, GameHours can recover the relationship only from the child's actual parent PID and a matching process identity observed during the previous 30 seconds; temporal proximity alone is not enough. Once verified, the child exact path is learned for future launches.

The desktop persists useful low-confidence candidates in SQLite and exposes them through **Pendientes (N)**. The review window shows the executable, local path, confidence, method, observation count and individual evidence. A decision can create a new game, associate the executable with an existing game, classify it as a launcher/helper/anti-cheat/updater/crash handler, or ignore it. Those decisions are local and durable; simply appearing as a candidate never counts playtime.

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

The repository also contains a Windows GitHub Actions workflow that performs restore, Release build and full solution tests on pushes to `feat/desktop-foundation` and on pull requests.

### Run the desktop shell

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj
```

The desktop uses the existing `%LOCALAPPDATA%\GameHours\gamehours.db` database and starts tracking automatically. Closing the window hides it to the notification area; the tray menu or the in-window **Salir de GameHours** action performs the graceful tracker shutdown before the process exits.

`--background` starts directly in the tray and is the argument used by the per-user Windows autostart setting:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj -- --background
```

Low-confidence graphical candidates are collected in the background without being counted as playtime. Open **Pendientes** in the main navigation to review them. The candidate center also has **Añadir EXE…** for games that expose no useful automatic signal at all.

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

The mapping is local-only. Future launches of that exact executable resolve with `learned_executable_path`. The desktop candidate center is now the normal graphical path for the same type of decision.

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

## Achievements

Compatible local achievement sources are normalized into one GameHours model. A complete catalogue is kept distinct from partial unlock-only state, and official Steam installations are deliberately isolated from emulator-compatible saves that happen to share an AppID.

Project P.I.T.T. has been validated with local GSE/Goldberg data at 4 of 23 unlocked achievements without Steam Web API or Internet access. See [`docs/ACHIEVEMENTS.md`](docs/ACHIEVEMENTS.md) for the compatibility architecture and current validation boundary.

## Calendar and statistics

**Calendario** and **Estadísticas** are normal sections of the main desktop navigation instead of tray-only auxiliary windows. Their embedded WPF views reuse the existing `DesktopActivityCalendarService` and `DesktopStatisticsService` local read models and are created on demand so starting GameHours in the tray does not calculate analytics unnecessarily.

The calendar groups measured playtime, achievement unlocks and 100% completion milestones by day, supports month navigation and day drill-down, and deliberately does not invent daily SRUM precision that the historical source cannot provide. Statistics expose monthly measured activity plus lifetime known playtime, measured/historical breakdown, most-played game/day, longest measured session, completion count, first known activity and activity streaks.

The integrated layout and interactions still need a real-machine visual/usability pass, especially at the desktop minimum window size. The underlying data model remains unchanged and CI builds/tests the integrated desktop slice.

## Installer and self-updates

GameHours has an isolated Velopack 1.2.0 update implementation. `GameHours.Core` owns only the update contract; `GameHours.Update` owns Velopack-specific behavior.

The Windows package publishes `GameHours.Desktop` and uses `GameHours.Desktop.exe` as the Velopack main executable.

Example beta package:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.2.0 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.2.0.md `
    -UpdateSource "C:\path\to\artifacts\velopack\beta"
```

The desktop exposes `Ajustes -> Actualizaciones`, performs silent startup/six-hour checks, can notify through the tray, shows release notes in-app, downloads only when requested and uses the normal graceful tracker shutdown before handing off a prepared update to Velopack.

A previous real Windows smoke test validated the underlying install/delta/download/restart mechanism and preservation of the existing GameHours database. The new WPF-as-package-entry-point flow still needs its own real-machine smoke test.

See [`docs/UPDATES.md`](docs/UPDATES.md).

## Next vertical slices

1. keep real-machine validation for expanded achievements, packaged updates, Windows detection/process-family/grace signals, the candidate-center workflow and embedded Calendar/Statistics explicitly pending while development continues;
2. synchronize one measured session end-to-end with Gestor de Juegos through the existing sync boundary, using a testable/local transport path until real backend validation is available;
3. add desktop authentication/sync status;
4. production update hosting and release automation;
5. Windows code signing before public distribution.

## Privacy direction

Raw SRUM databases, registry data, PIDs, process relationships, candidate evidence, user role decisions and full machine paths are local implementation details. The backend integration should receive only the minimum normalized information needed to associate playtime with a game and account.
