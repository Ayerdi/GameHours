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

1. acquire a disposable, consistent SRUM snapshot when a live read is not available;
2. inspect/parse only the live read-only database or snapshot;
3. normalize useful application evidence locally;
4. persist only normalized historical evidence;
5. delete any raw disposable snapshot.

The production client must never repair, modify, detach or otherwise mutate the original Windows SRUM database.

A one-time UAC-elevated helper is acceptable for historical acquisition if Windows file permissions/locking require it. Ongoing GameHours tracking remains non-admin. Any recovery or repair operation must target only a disposable copy.

## Real-machine schema validation

The development command:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-inspect
```

successfully opened the live SRUM database read-only on the first tested Windows 11 host. Nine tables were present. The relevant application-resource provider was:

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

## Read-only historical preview

The next development command reads Application Resource Usage records, resolves `AppId`, filters to the current Windows user SID, discards rows whose SRUM timestamp is after the immutable GameHours cutover, and aggregates `FaceTime` by application:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-preview
```

A text filter can narrow the preview without changing any state:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-preview gothic
```

The live SRUM path can be overridden for a disposable/offline copy with:

```powershell
$env:GAMEHOURS_SRUM_PATH = "C:\path\to\SRUDB.dat"
```

`FaceTime` from this provider is a 64-bit duration in 100 ns units, so it maps directly to .NET `TimeSpan` ticks. The preview does **not** write `HistoricalEvidence`; its purpose is to validate application resolution, user filtering, units and cutover behavior against real data before persistence is enabled.

## Importer plan

After preview values are validated against known games, the importer will:

1. resolve SRUM application IDs to executable/application identifiers;
2. match candidate executables to canonical GameHours game IDs;
3. exclude known crash reporters/helpers rather than adding their counters to the game;
4. clamp historical baseline coverage to `tracking_started_at`;
5. aggregate foreground/focus duration as estimated evidence rather than synthesizing fake historical sessions;
6. use stable/idempotent evidence identities so rerunning import does not duplicate playtime;
7. retain raw SRUM data only long enough to perform local normalization.

The importer should continue to prefer schema/column capability detection because SRUM provider GUIDs and table shapes have varied across Windows releases.

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
