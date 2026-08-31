# SRUM historical playtime

GameHours uses Windows SRUM only as historical/recovery evidence. It is never blindly added on top of measured GameHours sessions.

## Why SRUM is useful

Windows stores application resource-usage history in the ESE database:

```text
%WINDIR%\System32\sru\SRUDB.dat
```

Depending on the Windows version, SRUM exposes application/resource tables containing application IDs, timestamps and duration-like counters such as foreground/focus time. This can provide a useful estimate both for games played before GameHours started tracking and, conservatively, for uncovered periods after that cutover.

SRUM counters are not equivalent to an exact process lifetime. In particular, foreground/focus time is stored as `PlaytimeMetric.Foreground` with `Confidence.Estimated`, never as an exact measured session. Confidence remains an accounting property; the desktop UI presents source, estimated duration and evidence coverage rather than pretending to know an exact session boundary.

## Timeline invariant

GameHours keeps measured time and reconstructed evidence separate:

```text
SRUM baseline                 tracking_started_at       measured sessions
[============================]|[===]   [========]   [========>
                              |     \___/
                              |   uncovered interval
                              |       ↓
                              | SRUM GapRecovery only when safe
```

- baseline evidence must end at or before the immutable tracker cutover;
- measured sessions at/after the cutover remain authoritative;
- SRUM is not continuously accumulated alongside live tracking;
- post-cutover SRUM is considered only as `GapRecovery` for evidence that GameHours can associate conservatively with a known canonical game;
- gap evidence must not overlap a measured GameHours session;
- gap evidence must not overlap already persisted historical evidence;
- GameHours does not partially subtract a potentially overlapping SRUM bucket or invent exact start/end times merely to recover more duration.

The persistence layer enforces these rules through `HistoricalEvidence`, `PlaytimeTimelineRules` and `SqliteHistoricalEvidenceRepository`.

## Acquisition is separate from parsing

The live `SRUDB.dat` is normally owned and maintained by Windows. GameHours opens it read-only; if a Windows build cannot safely provide a live read, acquisition must use a disposable consistent snapshot rather than mutate the original database.

GameHours treats acquisition and parsing as separate stages:

1. resolve a read-only SRUM source;
2. inspect/parse only the live read-only database or a disposable snapshot;
3. normalize useful application evidence locally;
4. persist only normalized game-level historical evidence after explicit confirmation;
5. never repair or mutate the original Windows SRUM database.

### Desktop source resolution

`GAMEHOURS_SRUM_PATH` remains an explicit override for development or an offline/disposable copy. Without an override, the desktop client now resolves the native Windows source using these candidates in order:

1. `Environment.SystemDirectory\sru\SRUDB.dat`;
2. the Windows special folder plus `System32\sru\SRUDB.dat`;
3. `%WINDIR%\System32\sru\SRUDB.dat`.

The first candidate that actually exists is used. This avoids depending on one representation of `System32` when Windows/.NET path resolution differs between process environments.

The production client must never repair, modify, detach or otherwise mutate the original Windows SRUM database. Ongoing GameHours tracking remains non-admin.

## Real-machine schema validation

The development command:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
```

successfully opened the live SRUM database read-only on tested Windows 11 hosts. Nine tables were present. The relevant application-resource provider was:

```text
{D10CA2FE-6FCF-4F6D-848E-B2E99266FA89}
```

with the expected columns:

```text
AppId
UserId
TimeStamp
FaceTime
ForegroundCycleTime
BackgroundCycleTime
...
```

`SruDbIdMapTable` was also present with `IdType`, `IdIndex` and `IdBlob`, allowing `AppId` and `UserId` to be resolved to application identifiers and Windows SIDs.

The parser still validates required columns instead of assuming that every future Windows version exposes an identical schema.

A real-machine investigation on 2026-08-27 additionally proved that the CLI could open `C:\Windows\System32\sru\SRUDB.dat` while the desktop workflow initially failed to locate the same source unless `GAMEHOURS_SRUM_PATH` was supplied explicitly. The desktop resolver change is automatically verified but still requires a final real-machine run without the environment override before the path issue is considered closed.

## Raw preview and development commands

The historical development preview reads Application Resource Usage records, resolves `AppId`, filters to the current Windows user SID and can restrict rows to the immutable GameHours cutover:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-preview gothic
```

The live SRUM path can be overridden for a disposable/offline copy with:

```powershell
$env:GAMEHOURS_SRUM_PATH = "C:\path\to\SRUDB.dat"
```

`FaceTime` from this provider is a 64-bit duration in 100 ns units, so it maps directly to .NET `TimeSpan` ticks.

The desktop recovery workflow reads the current user's rows once, then partitions them into pre-cutover baseline rows and post-cutover gap candidates. It does not scan SRUM in the background.

## Conservative game normalization

Raw executable counters are not blindly summed. `srum-normalize` resolves NT device paths such as `\Device\HarddiskVolume3\...` to local drive paths, reuses the same helper classification as the live tracker, prefers exact learned executable mappings, and otherwise uses the normal game resolver.

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-normalize gothic
```

If several accepted primary executables for one canonical game report FaceTime in the same SRUM timestamp bucket, GameHours selects the largest row for that bucket rather than adding overlapping process counters.

Real-machine validation produced:

- Gothic 1 Remake: `53.766 h` from the main `G1R-Win64-Shipping.exe` executable;
- `CrashReportClient.exe`: `1.667 h`, excluded as a helper;
- a separate unresolved Gothic root executable: `0.767 h`, excluded rather than guessed;
- Project P.I.T.T.: `4.067 h` through its exact learned mapping.

This prevented the incorrect alternative of adding all Gothic-related executables into roughly 56.2 hours.

### Avoid repeated classification work

A real desktop preview with roughly 28,000 SRUM rows exposed avoidable repeated repository/resolver work. Normalization now caches the classification of each resolved executable path for the lifetime of one normalization call. Repeated rows for the same path still produce their individual decisions/timestamps, but mapping/game resolution is performed once per path instead of once per row.

This is an in-memory per-call cache only: it does not persist classification state, add polling or change resolution semantics.

## Guarded baseline import

After reviewing normalized output, the development CLI can persist only the explicitly filtered result:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- `
    srum-normalize --import gothic
```

The CLI requires a text filter so a broad historical import cannot happen by accident.

The desktop application exposes recovery from the notification-area menu:

```text
GameHours tray
  -> Recuperar historial de Windows…
```

Opening the window performs only a read-only preview. No write occurs until the user presses **Importar seleccionados**. The desktop preview intentionally analyzes SRUM only when this window is opened; it is not part of process, input or suspend observation.

Each accepted pre-cutover game baseline becomes one `HistoricalEvidence` item with:

```text
source = Srum
kind = Baseline
metric = Foreground
confidence = Estimated
```

The evidence identity is deterministic from the canonical game ID and immutable `tracking_started_at` cutover. Re-running the same import therefore returns the existing item instead of duplicating playtime.

The baseline coverage uses the observed SRUM window, expands backwards only when necessary to contain the sampled FaceTime duration, and never extends past `tracking_started_at`. No measured `sessions` rows are fabricated or modified.

## Conservative post-cutover gap recovery

The desktop workflow now also evaluates SRUM rows recorded after `tracking_started_at`, but under stricter rules than the baseline.

For each accepted canonical game and SRUM timestamp bucket:

1. helper/launcher-like processes remain excluded by the normal resolver;
2. if several accepted executable rows share the same timestamp, only the largest `FaceTime` row is considered;
3. the sample becomes `EvidenceKind.GapRecovery`, `PlaytimeMetric.Foreground`, `Confidence.Estimated`;
4. the SRUM timestamp is treated as a sample/flush boundary, not an exact session end;
5. GameHours assigns a conservative uncertainty window ending at that timestamp, normally one hour wide, enlarged only if needed to contain a larger reported `FaceTime`;
6. a bucket that would require pre-cutover duration is rejected;
7. any potential overlap with a measured session causes the whole bucket to be skipped;
8. any overlap with existing or already planned historical evidence also causes the bucket to be skipped;
9. persistence revalidates those overlap rules at write time in case the timeline changed after preview.

GameHours deliberately does **not** subtract a measured sub-interval from an SRUM bucket or invent a narrower start time to make a recovery fit. Losing uncertain coverage is preferable to double-counting or presenting false precision.

Gap recovery is currently limited to games that already have a canonical GameHours identity. Historical evidence alone does not silently create a new game.

The UI distinguishes these rows from the historical baseline with wording such as `Hueco posterior` / `Recuperable`, and labels the duration as estimated. Importing a gap creates historical evidence only; it never creates or edits a measured `PlaySession`.

### Current real-machine validation target

The 2026-08-27 `Click the Button` case is the first explicit real-machine validation target:

- the executable was manually confirmed and therefore has an exact learned mapping;
- it was played after `tracking_started_at` while GameHours was not running;
- the previous baseline-only UI correctly omitted it because all post-cutover rows were discarded;
- the new implementation should show it as recoverable only if SRUM actually contains a compatible post-cutover sample and no measured/historical interval conflicts with its conservative coverage window.

This behavior is automatically verified at the policy/repository level but remains pending real Windows validation. Absence of a candidate is not by itself a bug: SRUM may have no usable row, the row may not resolve safely, or its uncertainty window may overlap authoritative evidence.

## Privacy

Raw SRUM records may reveal unrelated applications, user identifiers and local paths. They are local implementation details and must not be synchronized to Gestor de Juegos.

The desktop SRUM workflow keeps those raw rows inside the transient analysis service. The candidate window receives only normalized game-level results. Only normalized evidence needed for playtime accounting survives an explicit import, for example:

```text
game_id
source = srum
kind = baseline | gap_recovery
metric = foreground
confidence = estimated
period_start_utc
period_end_utc
duration
```
