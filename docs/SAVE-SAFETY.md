# Save Safety

Save Safety reuses Ludusavi's mature save-location and scanning engine behind a GameHours-owned process boundary. The user does not need a separate Ludusavi installation, and the desktop application does not consume unstable Ludusavi Rust types directly.

## SaveEngine boundary

`GameHours.SaveEngine` is a small one-shot Rust executable shipped at:

```text
tools/GameHours.SaveEngine.exe
```

GameHours starts one helper process for one request. Requests are written as one JSON document to standard input; exactly one JSON response is read from standard output. The helper has no tray icon, resident daemon, independent settings UI or automatic background behavior.

The .NET boundary is `GameHours.SaveSafety.SaveEngineClient`. It owns:

- process start/termination;
- caller cancellation;
- a default 15-second timeout;
- a 1 MiB stdout limit and 64 KiB stderr limit;
- protocol/request ID validation;
- conversion of helper failures to structured `SaveEngineException.Code` values.

## Protocol v1

Request envelope:

```json
{
  "protocolVersion": 1,
  "requestId": "opaque-caller-id",
  "operation": "getCapabilities",
  "payload": {}
}
```

Successful response:

```json
{
  "protocolVersion": 1,
  "requestId": "opaque-caller-id",
  "ok": true,
  "result": {}
}
```

Failure response:

```json
{
  "protocolVersion": 1,
  "requestId": "opaque-caller-id",
  "ok": false,
  "error": {
    "code": "UnsupportedGame",
    "message": "..."
  }
}
```

The envelope is owned by GameHours. Upstream Ludusavi JSON structures are translated at the Rust boundary and are not part of the .NET contract.

### `getCapabilities`

Reports the GameHours engine version, protocol version, exact Ludusavi version/revision and supported operations. Packaging smoke tests execute this operation from the published helper and verify the pinned revision.

### `previewSaveData`

Input:

- path to an upstream-compatible manifest;
- exact manifest game name;
- one or more GameHours-supplied launcher roots (`path` + store kind).

Output:

- detected file paths and sizes;
- total file count/bytes for the complete backup payload;
- detected registry key names/count;
- per-file ignored/failed state.

`fileCount` is deliberately a technical payload count, **not** a count of user save slots or
playthroughs. A manifest entry can cover a directory containing the actual save, thumbnails and
other companion files, and a launcher root can expose synchronized copies as well. Desktop UI must
therefore describe this value as associated/protectable files rather than as "number of saves".

The implementation calls Ludusavi's backup scanner with `Finality::Preview`. It does **not** create a backup, restore data, write save files, update a manifest or enable automatic backups. Save Safety 1 is deliberately read-only.

### `previewGameSaveData`

Save Safety 2 adds stable game mapping before preview. The request supplies a store identity instead of a manifest title. The helper resolves Steam AppID or GOG game ID against the pinned manifest, refuses unsupported identities and returns `AmbiguousGame` if more than one manifest entry claims the same ID. Only after that exact mapping succeeds does it run the same `Finality::Preview` scan.

Desktop currently uses only installed-game identities already discovered by GameHours. Epic and loose/manual games are not title-guessed in this slice.

### `createGameBackup`

Save Safety 2 also exposes an explicit manual backup operation. It re-resolves the same stable store identity and re-scans current save data before writing; the preview is advisory and is never treated as a stale file list to copy. Desktop only enables **Crear copia ahora** after a successful preview for the currently selected game.

The desktop chooses a fixed GameHours-owned destination under `%LOCALAPPDATA%\GameHours\save-safety`. The helper rejects relative destinations and paths containing parent traversal, disables Ludusavi cloud synchronization and delegates the actual layout/write operation to Ludusavi with `Finality::Final`. The operation uses a longer two-minute client timeout than read-only preview because real saves can be large.

Once a manual write has started, normal game-detail navigation does not cancel it. Desktop cancels stale read-only previews when the selected game changes, but lets the active backup finish and persist its result before allowing another Save Safety preview or backup. This avoids terminating Ludusavi while it is copying a Simple-format backup into the shared GameHours-owned destination.

GameHours persists only the latest manual-operation state per game in SQLite schema v8: latest attempt, latest fully successful attempt, status/error code, payload file count/bytes and whether the scan contained changes. It deliberately does **not** store raw save paths or create its own backup-history/retention index; Ludusavi owns the backup layout and later Save Safety slices own history/retention UX.

## Upstream and licensing

The exact Ludusavi pin is recorded in `src/GameHours.SaveEngine/UPSTREAM.md`. Save Safety 2 also pins the primary `ludusavi-manifest` dataset in `src/GameHours.SaveEngine/MANIFEST-UPSTREAM.md` and packages a deterministic sanitized snapshot as `tools/ludusavi-manifest.yaml` for offline previews. The sanitizer removes only `launch` blocks, which are outside the save-scanning surface and can contain historical launcher credentials; the package and CI verify the resulting SHA-256.

Distributed notices are generated/verified from the Windows Cargo dependency graph:

```powershell
./scripts/generate-save-engine-notices.ps1
./scripts/generate-save-engine-notices.ps1 -Check
```

The packaged files are:

- `THIRD-PARTY-NOTICES.md` — GameHours-level attribution and the Ludusavi/MPL notes;
- `THIRD-PARTY-RUST-LICENSES.txt` — generated package inventory plus license/notice texts for the Rust dependencies linked into the Windows helper.

## Deliberately deferred

Save Safety still does not yet provide:

- post-session automatic backup;
- backup history/retention UI;
- restore or destructive operations;
- automatic title-based mapping for stores without a stable manifest ID.

Those belong to later Save Safety slices after the manual path is proven stable.
