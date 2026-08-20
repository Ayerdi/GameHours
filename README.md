# GameHours

GameHours is a local-first Windows playtime engine designed to discover games, recover useful historical playtime and measure future sessions even when a game is not launched through Steam or another supported launcher.

The long-term product is the desktop companion for **Gestor de Juegos**. The tracking engine remains deliberately independent so it can work offline and could later be reused by a standalone client.

## Current status

The repository now contains a working local foundation plus the first live-tracking/discovery/update slices:

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
- local session persistence with `High` confidence for reconciliation-based boundaries;
- isolated Velopack-based Windows installer/self-update foundation with beta/stable packaging support.

Real-machine testing confirmed automatic loose-game detection and exact-path learning for Gothic 1 Remake, manual mapping/tracking for Project P.I.T.T., canonical multiprocess tracking, and checkpoint-based interrupted-session recovery.

## Architecture

```text
GameHours.App          development host / future desktop shell
      |
      +-- GameHours.Core       domain, discovery/update contracts, session engine, timeline rules
      +-- GameHours.Windows    launcher discovery, process monitor, runtime resolver
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

This bounds normal crash loss to a few seconds while avoiding accidental counting of sleep, reboot or prolonged tracker downtime. The same client-generated session UUID is reused during recovery, so retrying recovery is idempotent.

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

### Track playtime locally

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- track
```

Leave it running and launch/close a detected game. Active sessions are checkpointed locally every five seconds. Completed and recovered measured segments are persisted in `%LOCALAPPDATA%\GameHours\gamehours.db`.

## Installer and self-updates

GameHours now has an isolated Velopack 1.2.0 update implementation. `GameHours.Core` owns only the update contract; `GameHours.Update` owns Velopack-specific behavior.

Create a local beta installer/feed:

```powershell
.\scripts\package-windows.ps1 -Version 0.1.0 -Channel beta
```

The package is self-contained `win-x64`, uses Velopack package id `Ayerdi.GameHours`, and is written under `artifacts\velopack\beta`.

Installed builds can inspect and apply a local or HTTP(S) Velopack feed:

```powershell
GameHours.App.exe update-check "C:\path\to\releases"
GameHours.App.exe update-now   "C:\path\to\releases"
```

`dotnet run` builds intentionally refuse self-update operations. Pending updates are not auto-applied at startup: the future desktop shell will coordinate update application with clean tracking shutdown/checkpointing.

See [`docs/UPDATES.md`](docs/UPDATES.md) for the architecture and the `0.1.0 -> 0.1.1` local smoke test.

## Next vertical slices

1. validate the packaged `0.1.0 -> 0.1.1` update flow on the real Windows host;
2. graphical unresolved-candidate/executable-role UI;
3. SRUM importer with strict cutover/gap recovery;
4. one real session synchronized end-to-end with Gestor de Juegos;
5. desktop UI/tray/autostart plus graphical update notifications/settings;
6. Windows suspend/resume-aware tracking hardening;
7. production update hosting, CI and Windows code signing.

## Privacy direction

Raw SRUM databases, registry data, PIDs and full machine paths are local implementation details. The backend integration should receive only the minimum normalized information needed to associate playtime with a game and account.
