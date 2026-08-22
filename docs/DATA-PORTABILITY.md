# Data portability and recovery

GameHours is local-first, so the user's data must remain recoverable without any account or external service. This document defines two deliberately different artifacts:

1. a **full SQLite backup** for exact recovery;
2. a **portable JSON export** for long-term ownership/interchange of domain data.

They are not interchangeable.

## Full SQLite backup

A backup is an exact database snapshot. It preserves every SQLite table, including machine-specific state such as executable mappings, candidate decisions and sync/outbox state. Since schema v4, it also preserves the per-session focused/active playtime telemetry stored in `session_activity`.

GameHours does **not** copy `gamehours.db` with a normal file copy. The live database uses WAL, so a raw copy of the main file can miss committed pages that are still represented in the WAL file. `GameHoursDataPortabilityService` uses SQLite's online backup API instead, then runs `PRAGMA integrity_check` against the produced snapshot before atomically moving it into the requested destination.

That means a backup can be created while the database is available to the application and represents one consistent SQLite snapshot.

Default development utility usage:

```powershell
dotnet run --project src/GameHours.Portability/GameHours.Portability.csproj -- backup
```

By default this reads:

```text
%LOCALAPPDATA%\GameHours\gamehours.db
```

and writes a timestamped file under:

```text
%LOCALAPPDATA%\GameHours\backups\
```

An explicit destination can be supplied:

```powershell
dotnet run --project src/GameHours.Portability/GameHours.Portability.csproj -- backup "D:\Backups\gamehours.db"
```

A different source database can be used for validation/testing:

```powershell
dotnet run --project src/GameHours.Portability/GameHours.Portability.csproj -- backup "D:\Backups\gamehours.db" --database "C:\path\gamehours.db"
```

### Controlled restore

The desktop **Ajustes → Copias y portabilidad → Restaurar copia…** flow performs restore as a lifecycle operation rather than as a raw file replacement.

The sequence is deliberately conservative:

```text
select backup
     |
     v
user confirmation
     |
     v
stop/dispose DesktopHost
(finalize active measured session)
     |
     v
snapshot + validate selected SQLite backup
     |
     v
migrate only a staging copy
     |
     v
create pre-restore safety backup of current database
     |
     v
remove stale WAL/SHM sidecars
     |
     v
atomic live-database replacement
     |
     v
initialize + PRAGMA integrity_check
     |
     v
restart GameHours
```

Important guarantees:

- an unreadable/corrupt selected file fails before the live database is touched;
- a backup from a newer unsupported schema fails while still in staging;
- the currently installed database is backed up before replacement;
- migration runs against a staging copy, not directly against the user's selected file;
- because migration can use WAL, GameHours takes a second SQLite snapshot after migration so the replacement file is self-contained;
- if a failure occurs after replacement, GameHours attempts automatic rollback from the pre-restore safety backup;
- GameHours restarts after a restore attempt so no disposed tracker/repository state remains in the running desktop process.

Pre-restore safety copies are stored under:

```text
%LOCALAPPDATA%\GameHours\backups\pre-restore-*.db
```

There is intentionally no standalone CLI restore command. Exact restore is exposed through the desktop lifecycle so GameHours can guarantee that the tracker and achievement monitor have stopped before the database is replaced.

## Portable JSON export

The JSON export is not a byte-for-byte backup. It is a versioned, backend-neutral representation of the durable GameHours domain data that should remain useful across machines and future integrations.

Create one from **Ajustes → Copias y portabilidad → Exportar JSON…** or with the development utility:

```powershell
dotnet run --project src/GameHours.Portability/GameHours.Portability.csproj -- export
```

Default output is a timestamped JSON file under:

```text
%LOCALAPPDATA%\GameHours\exports\
```

An explicit destination/source can be supplied in the same way as `backup`.

## Export format v1

Top-level shape:

```json
{
  "format_version": 1,
  "exported_at_utc": "2026-08-22T18:00:00Z",
  "source_schema_version": 4,
  "tracking_started_at_utc": "2026-08-20T18:00:00Z",
  "games": [],
  "sessions": [],
  "historical_evidence": [],
  "achievement_observations": [],
  "achievements": [],
  "achievement_completion_milestones": []
}
```

### Games

```json
{
  "id": "gamehours-game-uuid",
  "title": "Game title",
  "created_at_utc": "...",
  "updated_at_utc": "..."
}
```

The legacy/external `catalog_game_id` column is deliberately not part of the portable format.

### Measured sessions

```json
{
  "id": "client-session-uuid",
  "game_id": "gamehours-game-uuid",
  "started_at_utc": "...",
  "ended_at_utc": "...",
  "duration_milliseconds": 1800000,
  "capture_method": "reconciliation",
  "confidence": "high",
  "end_reason": "process-exit"
}
```

### Historical evidence

```json
{
  "id": "evidence-uuid",
  "game_id": "gamehours-game-uuid",
  "source": "srum",
  "evidence_kind": "baseline",
  "metric": "foreground",
  "confidence": "estimated",
  "period_start_utc": "...",
  "period_end_utc": "...",
  "duration_milliseconds": 900000
}
```

Achievement observation/catalogue state and completion milestones are exported with the same GameHours `game_id` identity and their normalized UTC timestamps.

## Intentionally excluded from portable export

The portable JSON does not contain:

- executable mappings or full executable paths;
- pending/resolved executable candidates and their evidence;
- PIDs/process relationships;
- Windows usernames/SIDs;
- raw SRUM or registry values;
- open-session checkpoints;
- focused/active session telemetry from `session_activity`;
- sync/outbox transport state;
- external catalogue IDs such as Gestor de Juegos IDs.

Those values either belong to one specific Windows installation, are transient implementation state, are not part of the stable v1 interchange contract, or are integration-specific. The full SQLite backup retains them when exact recovery is required.

Focused/active telemetry was introduced after portable format v1 had already been declared stable. GameHours therefore does **not** silently add it to v1. A future portable-format version can carry these metrics with explicit compatibility/import semantics.

## Portable JSON import v1

The desktop **Ajustes → Copias y portabilidad → Importar JSON…** flow merges portable domain data into the current GameHours database. It is intentionally different from exact restore: it does not replace machine-specific mappings, candidates or other local implementation state.

Import is split into two phases:

```text
select portable JSON
        |
        v
AnalyzeAsync
(read-only transaction)
        |
        +--> preview additions / updates / duplicates
        |
        +--> any conflict => stop, zero writes
        |
        v
user confirmation
        |
        v
briefly stop tracker
(finalize active session)
        |
        v
ImportAsync
(rebuild plan inside write transaction)
        |
        +--> conflict after revalidation => rollback
        |
        v
single SQLite commit
        |
        v
refresh local views + restart tracker
```

`AnalyzeAsync` never writes. `ImportAsync` does not trust a stale preview: it reads the current database again and rebuilds the same import plan inside the transaction immediately before applying it.

### Timeline and identity rules

Import v1 is deliberately conservative:

- an existing local `tracking_started_at` is never moved by import;
- if the target has no cutover yet, the source `tracking_started_at_utc` may initialize it;
- every imported measured session is validated against the effective cutover;
- baseline evidence must remain on the historical side of the cutover;
- gap-recovery evidence must remain on the measured side and cannot overlap measured sessions;
- a new measured session that overlaps another measured session for the same game is rejected rather than double-counted;
- overlapping historical intervals for the same game are rejected rather than guessed/combined;
- the same session/evidence UUID with identical normalized content is an idempotent duplicate and is ignored;
- the same session/evidence UUID with different content is a conflict;
- a game title already present under a different GameHours UUID is an identity conflict in v1; the importer does not guess which UUID should become canonical.

If any conflict exists anywhere in the file, the import is blocked before writing. The Settings preview shows the additions, updates, duplicates and conflict count and surfaces the first conflict details.

### Achievement merge rules

Achievement state is merged monotonically rather than replaced blindly:

- an already unlocked achievement cannot become locked through import;
- known unlock/first-observed timestamps preserve the earliest useful time;
- `first_seen_at_utc` moves only earlier and `last_seen_at_utc` only later;
- newer useful metadata can enrich an existing achievement;
- observation state preserves the earliest initialization, latest observation and whether a complete catalogue has ever been seen;
- completion milestones prefer exact timestamps over observation-time fallbacks.

### Runtime boundary

The preview can be generated while normal monitoring continues. Immediately before the actual merge, the desktop briefly stops the tracker so a currently active game session is finalized into SQLite. The importer then revalidates against that updated state, commits atomically, refreshes the local views and resumes tracking. If final revalidation reveals a new conflict, the transaction is rolled back and the tracker is resumed without importing anything.

There is intentionally no standalone CLI import command yet. The desktop owns the tracker lifecycle needed to make the live merge boundary safe.

## Compatibility rule

`format_version` is the compatibility boundary. Adding optional fields may remain compatible within a version, but removing/renaming fields or changing their meaning requires a new export format version. The SQLite schema version is recorded separately as diagnostic provenance and must not be treated as the JSON format version.
