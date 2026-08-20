# SRUM historical playtime

GameHours uses Windows SRUM only as historical/recovery evidence. It is never blindly added on top of measured GameHours sessions.

## Why SRUM is useful

Windows stores application resource-usage history in the ESE database:

```text
%WINDIR%\System32\sru\SRUDB.dat
```

Depending on the Windows version, SRUM exposes application/resource tables containing application IDs, timestamps and duration-like counters such as foreground/focus time. This can provide a useful estimate for games that were played before GameHours was installed.

SRUM counters are not equivalent to an exact process lifetime. In particular, foreground/focus time should be stored as `PlaytimeMetric.Foreground` with `Confidence.Estimated`, not as an exact measured session.

## Timeline invariant

For the initial historical baseline:

```text
SRUM evidence                    tracking_started_at          GameHours sessions
[===============================]|[==========================================>
```

- baseline evidence must end at or before the immutable tracker cutover;
- measured sessions at/after the cutover remain authoritative;
- SRUM is not continuously accumulated alongside live tracking;
- post-cutover SRUM is considered only for a specifically identified uncovered gap;
- gap evidence must not overlap a measured GameHours session.

The persistence layer already enforces these rules through `HistoricalEvidence`, `PlaytimeTimelineRules` and `SqliteHistoricalEvidenceRepository`.

## Acquisition is separate from parsing

The live `SRUDB.dat` is normally owned and locked by Windows. ESENT database files cannot generally be shared between separate ESENT processes, and a copied database may also be in a dirty-shutdown state.

GameHours therefore treats acquisition and parsing as separate stages:

1. acquire a disposable, consistent SRUM snapshot;
2. inspect/parse only the snapshot;
3. normalize useful application evidence locally;
4. persist only normalized historical evidence;
5. delete the raw snapshot.

The production client must never repair, modify, detach or otherwise mutate the original Windows SRUM database.

A one-time UAC-elevated helper is acceptable for historical acquisition if Windows file permissions/locking require it. Ongoing GameHours tracking remains non-admin. Any recovery or repair operation must target only a disposable copy.

## Current diagnostic slice

`GameHours.Windows` references Microsoft's `Microsoft.Database.ManagedEsent` package and exposes a read-only schema inspector.

Run against the default live path:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
```

Or an explicitly supplied offline copy:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- `
    srum-inspect "C:\path\to\SRUDB.dat"
```

This command deliberately runs before GameHours opens its SQLite database. It cannot alter `tracking_started_at`, sessions or mappings. It only prints the ESE table names and column names that are actually present on the tested Windows build.

The first real-machine run is intentionally exploratory. If the live database returns a lock or dirty-shutdown error, that is expected evidence that the next slice must be the disposable snapshot helper; GameHours must not work around the error by touching the original database.

## Parser plan

After the real schema is confirmed, the importer will:

1. read `SruDbIdMapTable` to resolve SRUM application IDs to executable/application identifiers;
2. detect the relevant application-resource table by required columns rather than assuming a single GUID forever;
3. read only records relevant to candidate game executables;
4. exclude known crash reporters/helpers and canonicalize executable mappings to one GameHours game ID;
5. clamp the historical baseline coverage to `tracking_started_at`;
6. aggregate foreground/focus duration as estimated evidence rather than synthesizing fake historical sessions;
7. use stable/idempotent evidence identities so rerunning import does not duplicate playtime.

The importer should prefer schema/column capability detection because SRUM provider GUIDs and table shapes have varied across Windows releases.

## Privacy

Raw SRUM records may reveal unrelated applications, user identifiers and local paths. They are local implementation details and must not be synchronized to Gestor de Juegos.

Only normalized evidence needed for playtime accounting should survive the import, for example:

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
