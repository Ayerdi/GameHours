# Architecture

## Product boundary

GameHours is the Windows tracking subsystem for the future Gestor de Juegos desktop application. It is intentionally capable of running without the Gestor backend.

```text
Windows
  |-- process snapshots / events
  |-- SRUM historical evidence
  |-- launcher manifests / executable metadata
  v
GameHours.Windows
  v
GameHours.Core
  |-- game resolution policy
  |-- session engine
  |-- playtime timeline policy
  v
GameHours.Storage (SQLite)
  |
  +--> future desktop UI
  +--> GameHours.Sync --> Gestor de Juegos API
```

## Dependency direction

`Core` depends on nothing else in this repository.

`Windows`, `Storage` and `Sync` depend on `Core` but not on each other unless a concrete composition root explicitly wires them together.

`App` is the composition root.

## Local identifiers

The local engine uses UUIDs for games, sessions and evidence. A game does not need a Gestor catalog ID to be tracked locally. Backend/catalog mapping is optional metadata resolved later.

This is important for offline use and for unknown executables.

## Process monitoring target design

The production monitor will be hybrid:

1. event-driven observation (ETW preferred; WMI can remain fallback/reference);
2. periodic process reconciliation;
3. initial snapshot on startup;
4. crash/restart recovery from persisted state/checkpoints.

The reconciliation layer is not temporary. The proof showed that WMI start/stop events can be missed while snapshots still observe the real process lifetime.

## Game identity

Filename-only resolution is forbidden as an authoritative identity key. Resolution should prefer, in order:

1. an existing full-path mapping;
2. launcher/store manifest identifiers;
3. PE product/file metadata + install directory context;
4. backend/catalog resolver;
5. user confirmation for ambiguous candidates.

Helper executables can map to the same game while being marked `is_helper` so their lifetime is not blindly added.

## Storage

SQLite is the local source of truth for:

- tracker cutover;
- local games and executable mappings;
- measured sessions;
- historical evidence;
- sync outbox.

WAL is enabled to make foreground UI reads coexist safely with tracker writes.
