# Architecture

## Product boundary

GameHours is the local Windows tracking subsystem for the future Gestor de Juegos desktop application. It is deliberately usable without that backend.

```text
Windows
  |-- process snapshots / parent relationships
  |-- launcher/store metadata
  |-- GameConfigStore / runtime evidence
  |-- SRUM historical evidence
  v
GameHours.Windows
  v
GameHours.Core
  |-- resolution / candidate policy
  |-- session engine
  |-- timeline rules
  v
GameHours.Storage (SQLite)
  |
  +--> GameHours.Desktop
  +--> GameHours.Sync --> Gestor de Juegos API
```

## Dependency direction

- `GameHours.Core`: domain, abstractions and policy; no Windows/SQLite/backend dependency.
- `GameHours.Windows`: Windows discovery, process observation, SRUM and local achievement readers.
- `GameHours.Storage`: SQLite schema/migrations and repositories.
- `GameHours.Desktop`: WPF/tray composition root for the installed application.
- `GameHours.Sync`: normalized backend boundary.
- `GameHours.Update`: Velopack implementation.
- `GameHours.App`: diagnostic CLI composition root.

## Process pipeline

The production desktop has one global process-observation path. `WindowsProcessSnapshotProvider` captures the process set and its parent/start-time metadata. `HybridWindowsProcessMonitor` performs permanent one-second reconciliation and observes process exits.

The same process identity feeds both tracking and candidate discovery:

```text
WindowsProcessSnapshotProvider
          |
          +--> RecentProcessIdentityHistory
          |
          v
HybridWindowsProcessMonitor
          v
WindowsGameResolver
          v
LearningGameResolver
          v
CandidateRecordingGameResolver
          v
GameSessionEngine
```

`CandidateRecordingGameResolver` is a passive decorator: it can persist a useful low-confidence result but cannot promote it or start a session. This avoids a second process scanner and guarantees the candidate UI explains the same decision the tracker actually made.

## Process identity history

The Windows snapshot captures parent PID once for the process set rather than running a full Toolhelp enumeration separately for each candidate.

A shared 30-second history stores:

- PID;
- normalized path;
- start time when available;
- parent PID when available;
- last-seen time.

Partial later observations merge with richer cached identity instead of erasing parent/start-time metadata. This allows a child to recover an already-exited launcher through its actual Windows parent PID while retaining PID-reuse checks.

## Game identity

Filename-only identity is never authoritative. Prefer:

1. exact learned path;
2. launcher/store identity and exact launch executable where known;
3. install-root context plus runtime evidence;
4. GameConfigStore / engine runtime evidence;
5. verified learned process-family relationship;
6. explicit user confirmation.

An install directory by itself is not sufficient to count an arbitrary executable. Unknown binaries below a known root stay candidates until stronger evidence exists.

Helper executables can map to the same game with `is_helper` so their lifetime is never blindly counted.

## Session lifecycle

`GameSessionEngine` groups all trackable processes for one game into one measured session and keeps it active until the last process exits.

The desktop creates a fresh engine when tracking starts/restarts. A completed/faulted tracking task is not treated as an active tracker. Unexpected engine failure leaves conservative durable checkpoints for recovery and clears stale desktop active-game/achievement-monitor state.

Refresh requests caused by closely timed session/achievement events are coalesced instead of launching overlapping full library reloads.

## Storage and migrations

SQLite is the local source of truth for:

- tracker cutover;
- games and executable mappings;
- measured/open sessions;
- historical evidence;
- normalized achievement state/milestones;
- unresolved candidates;
- sync outbox.

`GameHoursDatabase` owns schema initialization. `PRAGMA user_version` drives explicit structural migrations; repositories do not independently create feature tables. Safe data backfills remain idempotent and separate from schema version transitions.

WAL is enabled. Connection pooling stays disabled so repository operations release database files predictably on Windows. Read-heavy UI paths instead reduce overhead with bulk queries and in-memory grouping.

## Read models

Library, Calendar and Statistics use bulk session/evidence/mapping/achievement-summary reads rather than issuing several queries per game. This keeps SQLite interaction bounded as the local library grows while preserving the existing domain calculations and measured-vs-historical distinction.

## Local identifiers and privacy

Local UUIDs identify games, sessions and evidence. A game does not need a Gestor catalog ID to be tracked.

Raw SRUM, registry values, PIDs, process relationships, full executable paths and candidate evidence are local implementation details. Future sync should send only normalized data needed by Gestor de Juegos.
