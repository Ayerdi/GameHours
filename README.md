# GameHours

GameHours is a local-first Windows playtime engine designed to discover games, recover useful historical playtime and measure future sessions even when a game is not launched through Steam or another supported launcher.

The long-term product is the desktop companion for **Gestor de Juegos**. The tracking engine remains deliberately independent so it can work offline and could later be reused by a standalone client.

## Current status

The repository now contains a working local foundation plus the first live-tracking slice:

- SQLite persistence and immutable `tracking_started_at` cutover;
- timeline rules that prevent SRUM baseline/gap evidence from double-counting measured sessions;
- installed-game discovery for Steam, Epic and GOG;
- launcher-independent runtime detection for high-confidence Unreal and Unity executables;
- Windows process monitoring with process-exit events plus permanent one-second reconciliation;
- a session engine that keeps one game session active until its last primary process exits;
- local session persistence with `High` confidence for reconciliation-based boundaries.

The proof-of-concept phase previously validated SRUM foreground evidence, UserAssist as secondary evidence and a real 65.180-second Gothic session captured by reconciliation.

## Architecture

```text
GameHours.App          development host / future desktop shell
      |
      +-- GameHours.Core       domain, discovery contracts, session engine, timeline rules
      +-- GameHours.Windows    launcher discovery, process monitor, runtime resolver
      +-- GameHours.Storage    local SQLite persistence
      +-- GameHours.Sync       optional Gestor de Juegos sync boundary
```

The key rule is **local-first**: tracking and historical reconstruction must keep working without an Internet connection or backend availability.

## Game discovery

Detection is layered rather than launcher-dependent:

1. Steam manifests and library folders;
2. Epic `.item` manifests;
3. GOG registry entries;
4. conservative runtime signatures for loose Unreal/Unity games.

This means a copied/DRM-free game can still become trackable even if it has no Steam/Epic metadata. See [`docs/GAME-DISCOVERY.md`](docs/GAME-DISCOVERY.md).

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

This prints games discovered from installed launchers and high-confidence game processes already running.

### Track playtime locally

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- track
```

Leave it running, launch/close a detected game, then press `Ctrl+C` to stop GameHours. Completed sessions are persisted in `%LOCALAPPDATA%\GameHours\gamehours.db`.

## Next vertical slices

1. validate installed/runtime discovery against real machines and fix false positives/negatives;
2. durable open-session checkpoints and crash/reboot recovery;
3. executable role/mapping UI for unresolved games;
4. SRUM importer with strict cutover/gap recovery;
5. one real session synchronized end-to-end with Gestor de Juegos;
6. desktop UI/tray/autostart.

## Privacy direction

Raw SRUM databases, registry data, PIDs and full machine paths are local implementation details. The backend integration should receive only the minimum normalized information needed to associate playtime with a game and account.
