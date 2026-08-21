# SRUM historical playtime

GameHours uses Windows SRUM only as historical/recovery evidence. It is never blindly added on top of measured GameHours sessions.

## Why SRUM is useful

Windows stores application resource-usage history in the ESE database:

```text
%WINDIR%\System32\sru\SRUDB.dat
```

Depending on the Windows version, SRUM exposes application/resource tables containing application IDs, timestamps and duration-like counters such as foreground/focus time. This can provide a useful estimate for games that were played before GameHours was installed.

SRUM counters are not equivalent to an exact process lifetime. In particular, foreground/focus time is stored as `PlaytimeMetric.Foreground` with `Confidence.Estimated`, not as an exact measured session. Confidence remains an internal accounting property; the normal desktop UI presents source, known duration and evidence window rather than a user-facing confidence score.

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

The persistence layer enforces these rules through `HistoricalEvidence`, `PlaytimeTimelineRules` and `SqliteHistoricalEvidenceRepository`.

## Acquisition is separate from parsing

The live `SRUDB.dat` is normally owned and locked by Windows. GameHours first attempts a read-only open. If a Windows build does not permit that, acquisition must create a disposable consistent snapshot rather than mutating the original database.

GameHours therefore treats acquisition and parsing as separate stages:

1. acquire a disposable, consistent SRUM snapshot when a live read is not available;
2. inspect/parse only the live read-only database or snapshot;
3. normalize useful application evidence locally;
4. persist only normalized historical evidence;
5. delete any raw disposable snapshot.

The production client must never repair, modify, detach or otherwise mutate the original Windows SRUM database. Ongoing GameHours tracking remains non-admin.

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

## Raw historical preview

The raw preview reads Application Resource Usage records, resolves `AppId`, filters to the current Windows user SID, discards rows whose SRUM timestamp is after the immutable GameHours cutover, and aggregates `FaceTime` by application:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- srum-preview gothic
```

The live SRUM path can be overridden for a disposable/offline copy with:

```powershell
$env:GAMEHOURS_SRUM_PATH = "C:\path\to\SRUDB.dat"
```

`FaceTime` from this provider is a 64-bit duration in 100 ns units, so it maps directly to .NET `TimeSpan` ticks.

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

## Guarded baseline import

After reviewing normalized output, the development CLI can persist only the explicitly filtered result:

```powershell
dotnet run --project src/GameHours.App/GameHours.App.csproj -- `
    srum-normalize --import gothic
```

The CLI requires a text filter so a broad historical import cannot happen by accident.

The desktop application now exposes the same conservative normalization as an explicit recovery workflow from the notification-area menu:

```text
GameHours tray
  -> Recuperar historial de Windows…
```

Opening the window performs only a read-only preview. It shows the games GameHours can associate conservatively, recoverable historical duration, the retained evidence window and whether that game's SRUM baseline has already been imported. Candidates not yet imported are selected by default for convenience, but no write occurs until the user presses **Importar seleccionados**. Existing baselines are disabled and the underlying importer remains idempotent.

The desktop preview intentionally analyzes SRUM only when this window is opened. It is not part of the permanent one-second tracking loop and does not scan SRUM continuously in the background.

Each accepted game becomes one `HistoricalEvidence` item with:

```text
source = Srum
kind = Baseline
metric = Foreground
confidence = Estimated
```

The evidence identity is deterministic from the canonical game ID and immutable `tracking_started_at` cutover. Re-running the same import therefore returns the existing item instead of duplicating playtime.

The evidence coverage uses the observed SRUM window, expands backwards only when necessary to contain the sampled FaceTime duration, and never extends past `tracking_started_at`. No measured `sessions` rows are fabricated or modified.

The desktop recovery workflow is implemented but still needs real-Windows UI validation after the current desktop changes. The underlying SRUM parser, normalization rules and baseline import have already been validated on the first Windows test host.

## Future gap recovery

The initial importer creates only pre-cutover `Baseline` evidence. Post-cutover SRUM may later be used as `GapRecovery` only for explicitly identified tracker gaps, and the existing repository rejects gap evidence that overlaps measured GameHours sessions.

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
