# GameHours

GameHours is a local-first Windows playtime engine for games that are not reliably tracked by a launcher. It discovers local games/processes, measures future sessions, reconstructs compatible historical playtime and reads local achievement state. Integrations with external managers are optional adapters; GameHours must remain fully useful without them.

## Current status

The repository contains a working local foundation with:

- SQLite persistence with an immutable `tracking_started_at` cutover and versioned schema migrations;
- timeline rules that keep measured sessions separate from SRUM baseline/gap evidence and prevent double counting;
- Steam, Epic and GOG installed-game discovery;
- one shared Windows process-observation pipeline with periodic reconciliation, process-exit observation, parent PID/start-time identity history and suspend/resume boundaries;
- layered game detection using learned exact paths, launcher manifests/install roots, GameConfigStore, Unreal/Unity layouts, executable roles, graphics/window evidence and process relationships;
- conservative launcher/helper -> real-game learning, including exact-parent-PID recovery for launchers that exit early;
- durable five-second session checkpoints and conservative interrupted-session recovery;
- a graphical **Pendientes** workflow for low-confidence executables without running a second global process scanner;
- local achievement parsing/state, session-scoped notifications and Steam-local artwork enrichment;
- integrated **Biblioteca · Actividad · Calendario · Estadísticas · Pendientes · Ajustes** desktop navigation;
- bulk local read models for library/calendar/statistics rather than per-game SQLite query loops;
- a backend-neutral measured-session sync boundary using GameHours UUIDs, normalized UTC fields and persistent UUID idempotency through a local transport;
- Velopack installation/self-update support;
- Windows CI that restores, builds, tests and smoke-publishes the desktop application.

Real-machine testing has already confirmed loose-game tracking for Gothic 1 Remake, manual Project P.I.T.T. tracking, multiprocess sessions, checkpoint recovery, SRUM baseline import, graceful shutdown, suspend/resume segmentation, local GSE achievement parsing and the underlying Velopack update mechanism.

Expanded achievement providers, packaged WPF updating, the newer Windows evidence/process-family pipeline, **Pendientes**, and the embedded Calendar/Statistics UX still require a real-machine validation pass. That remains explicitly non-blocking while implementation continues.

## Architecture

```text
Windows process/store/runtime data
              |
              v
      GameHours.Windows
              |
              v
        GameHours.Core
       /      |       \
      v       v        v
 Storage   Desktop    Sync
 SQLite      WPF    neutral boundary
              |
              v
          Update
        Velopack

Optional external adapters live outside the tracking core.
```

`GameHours.Core` owns domain and policy. Windows-specific inspection, SQLite, WPF, neutral outbound sync, external adapters and Velopack stay outside Core.

The key rule is **local-first**: tracking, compatible history, achievements and the desktop experience must keep working without Internet, accounts or backend availability.

## Game discovery

Detection is evidence-based rather than launcher-dependent. Strong identity sources include:

1. an exact executable path already learned locally;
2. Steam/Epic/GOG installation metadata and known launch executables;
3. an exact Windows GameConfigStore executable;
4. packaged Unreal/Unity runtime layouts;
5. a verified child of a launcher/helper already mapped to a known game;
6. explicit user confirmation.

A known installation directory is useful context, but no longer makes every unknown EXE inside it automatically count as gameplay. A utility/config/benchmark/updater/helper stays excluded; an otherwise unknown executable inside that directory remains below the automatic tracking threshold until stronger runtime/identity evidence appears.

Direct3D/OpenGL/Vulkan plus a visible window is also deliberately insufficient on its own because normal desktop applications use GPU APIs. Such observations can become **Pendientes** candidates without creating a session.

### Process-family grace

The Windows snapshot collects PID, parent PID, executable path and process start time once for the process set and feeds a shared 30-second identity history before game resolution. That means an already learned launcher cannot bypass relationship history merely because `LearningGameResolver` resolves its exact path immediately.

If the parent has already exited, GameHours accepts only the child's actual Windows parent PID and a matching recent process identity. Start times are retained when later observations contain less metadata, so a reduced tracker snapshot cannot erase the PID-reuse guard. Temporal proximity alone never establishes a launcher/game relationship.

See [`docs/GAME-DISCOVERY.md`](docs/GAME-DISCOVERY.md).

## Pendientes

The authoritative resolver is wrapped by a candidate recorder. Every process is resolved once for tracking; the same result may also be persisted as a candidate when it has useful positive evidence but remains below the `0.80` automatic threshold.

This is intentionally one pipeline:

```text
Windows snapshot
      |
      v
 shared history
      |
      v
 game resolver
   /      \
  /        \
>= .80    useful < .80
 |             |
session     candidate
```

Candidate persistence never raises confidence and never starts playtime. The review center can create/associate a game, classify launcher/helper/anti-cheat/updater/crash roles or ignore an executable. **Añadir EXE…** remains available for games with no useful automatic signal.

## Playtime model

GameHours does not add unrelated counters together:

```text
past                         tracking_started_at                    future
-----------------------------------|-------------------------------------->
 SRUM baseline (~estimated)        | GameHours measured sessions
                                   |        [verified gap]
                                   |            + SRUM gap evidence
```

- baseline evidence cannot extend beyond the cutover;
- measured sessions are authoritative for intervals GameHours observed;
- gap recovery must not overlap measured sessions;
- a crash/interruption is recovered only through the last durable checkpoint;
- intentional shutdown finalizes the active session before exit;
- suspended Windows time is excluded rather than counted as gameplay.

See [`docs/PLAYTIME-TIMELINE.md`](docs/PLAYTIME-TIMELINE.md).

## Achievements

Compatible local achievement sources are normalized into one SQLite model. A complete catalogue remains distinct from partial unlock-only state, and official Steam data is isolated from compatible emulator saves that share the same AppID.

Project P.I.T.T. has been validated locally with GSE/Goldberg data at 4 of 23 achievements without Steam Web API or Internet access. See [`docs/ACHIEVEMENTS.md`](docs/ACHIEVEMENTS.md).

## Calendar and statistics

Calendar and Statistics are first-class views of the main WPF window. The old duplicate auxiliary implementations have been removed; tray shortcuts now navigate to the same embedded views.

Both are created on demand. Calendar allocates only measured sessions across local days and never invents daily precision for SRUM. Statistics keeps monthly measured activity separate from lifetime measured + historical totals.

Their data services bulk-load sessions/evidence/achievement summaries instead of issuing a group of SQLite queries for every game.

## SQLite lifecycle

`GameHoursDatabase` owns the schema. `PRAGMA user_version` drives explicit migrations; repositories no longer create feature tables independently. Data backfills that are safe and idempotent (for example an already-proven 100% achievement milestone) run separately from structural migrations.

SQLite connection pooling remains disabled deliberately on Windows so closed repository operations release database files predictably. The bulk read-model changes provide the useful performance win without retaining pooled file handles.

## Backend-neutral sync boundary

`GameHours.Sync` is an application-owned boundary, not a Gestor de Juegos client. It emits GameHours identities and normalized UTC data:

```text
SQLite measured session
        |
        v
PlaytimeSyncBatch
(GameHours UUIDs)
        |
        v
persistent local sync transport
        |
        +--> first delivery: accepted
        |
        +--> same client UUID retry: duplicate, no extra time
```

The neutral JSON shape uses `tracking_started_at_utc`, `client_session_id`, `game_id`, `started_at_utc`, `ended_at_utc`, `capture_method` and `confidence`. A measured session before the tracking cutover is rejected before transport. The local receiver persists accepted UUIDs and detects an idempotency conflict if a previously accepted UUID is retried with different data.

External systems are responsible for translating the GameHours `game_id` to their own catalogue identity and for implementing their own authentication. No external catalogue ID or backend-specific field name belongs in `GameHours.Core` or the neutral sync contract.

See [`docs/SYNC-BOUNDARY.md`](docs/SYNC-BOUNDARY.md). The optional deferred Gestor adapter notes live under [`integration/gestor-juegos/`](integration/gestor-juegos/).

## Development

Requirements: Windows 10/11 for Windows collectors and the .NET 8 SDK.

```powershell
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release
dotnet test GameHours.sln -c Release
```

CI additionally smoke-publishes `GameHours.Desktop` after the solution tests and cancels superseded runs for the same ref.

Run the desktop:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj
```

Start directly in the tray:

```powershell
dotnet run --project src/GameHours.Desktop/GameHours.Desktop.csproj -- --background
```

Useful diagnostic commands:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- scan
dotnet run --project src/GameHours.App/GameHours.App.csproj -- diagnose
dotnet run --project src/GameHours.App/GameHours.App.csproj -- track
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
dotnet run --project src/GameHours.App/GameHours.App.csproj -- map "C:\Games\ProjectPIIT.exe" "Project P.I.I.T."
```

## Installer and updates

GameHours isolates Velopack behind `IAppUpdateService`. Update checks now honor the caller cancellation token, downloads are explicit, and a prepared update uses the normal graceful tracker shutdown before Velopack replaces files.

Example package:

```powershell
.\scripts\package-windows.ps1 `
    -Version 0.2.0 `
    -Channel beta `
    -ReleaseNotes .\release-notes\0.2.0.md `
    -UpdateSource "C:\path\to\artifacts\velopack\beta"
```

See [`docs/UPDATES.md`](docs/UPDATES.md).

## Next vertical slices

1. add standalone data portability/recovery: safe local backup plus a documented export format before any external account becomes necessary;
2. keep real-machine validation for expanded achievements, packaged updates, Windows detection/process-family signals, candidate decisions and embedded analytics explicitly pending/non-blocking;
3. production update hosting/release automation;
4. Windows code signing before public distribution;
5. only after the standalone application is mature, resume optional external adapters such as Gestor de Juegos without changing the neutral GameHours contract.

## Privacy direction

Raw SRUM, registry values, PIDs, process relationships, candidate evidence, user role decisions and full machine paths remain local implementation details. Optional external adapters should send only normalized information required for their explicit integration purpose.
