# Data portability and recovery

GameHours is local-first, so the user's data must remain recoverable without any account or external service. This document defines two deliberately different artifacts:

1. a **full SQLite backup** for exact recovery;
2. a **portable JSON export** for long-term ownership/interchange of domain data.

They are not interchangeable.

## Full SQLite backup

A backup is an exact database snapshot. It preserves every SQLite table, including machine-specific state such as executable mappings, candidate decisions and sync/outbox state.

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

### Restore policy

There is intentionally no in-place restore command yet. Replacing the live database while the desktop tracker has open state is more dangerous than creating a backup. The backup is already independently openable and integrity-checked; an automatic restore flow should be added only together with an explicit desktop shutdown/restart boundary and pre-restore safety copy.

## Portable JSON export

The JSON export is not a byte-for-byte backup. It is a versioned, backend-neutral representation of the durable GameHours domain data that should remain useful across machines and future integrations.

Create one with:

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
  "source_schema_version": 3,
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
- sync/outbox transport state;
- external catalogue IDs such as Gestor de Juegos IDs.

Those values either belong to one specific Windows installation, are transient implementation state, or are integration-specific. The full SQLite backup retains them when exact recovery is required.

## Compatibility rule

`format_version` is the compatibility boundary. Adding optional fields may remain compatible within a version, but removing/renaming fields or changing their meaning requires a new export format version. The SQLite schema version is recorded separately as diagnostic provenance and must not be treated as the JSON format version.
