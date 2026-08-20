# GameHours

GameHours is a Windows playtime engine designed to discover games, recover useful historical playtime and measure future sessions even when a game is not launched through Steam or another supported launcher.

The long-term product is the desktop companion for **Gestor de Juegos**. The tracking engine remains deliberately independent so it can work offline and could later be reused by a standalone client.

## Current status

The proof-of-concept phase validated three important facts on Windows:

- SRUM can provide useful historical **foreground-time evidence** for a game executable.
- UserAssist can corroborate execution/focus evidence but is not reliable enough to be the primary historical counter.
- A game started outside GameHours can be detected and timed using process observation plus periodic reconciliation.

The repository is now moving from probes to the real .NET implementation.

## Architecture

```text
GameHours.App          development host / future desktop shell
      |
      +-- GameHours.Core       domain, timeline rules, abstractions
      +-- GameHours.Windows    Windows process/game discovery
      +-- GameHours.Storage    local SQLite persistence
      +-- GameHours.Sync       optional Gestor de Juegos sync boundary
```

The key rule is **local-first**: tracking and historical reconstruction must keep working without an Internet connection or backend availability.

## Playtime model

GameHours never blindly adds all available counters together.

```text
past                               tracking_started_at                         future
------------------------------------------|------------------------------------------>
        SRUM baseline (~estimated)        |     GameHours sessions (exact/high)
                                          |          [gap]
                                          |            + SRUM gap recovery (~estimated)
```

- `baseline` historical evidence must end at or before the tracker cutover.
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

## Next vertical slices

1. local domain + SQLite timeline foundation;
2. hybrid Windows process monitor (events + reconciliation);
3. executable-to-game resolution;
4. SRUM importer with a strict cutover;
5. one real session synchronized end-to-end with Gestor de Juegos;
6. desktop UI/tray/autostart.

## Privacy direction

Raw SRUM databases, registry data, PIDs and full machine paths are local implementation details. The backend integration should receive only the minimum normalized information needed to associate playtime with a game and account.
