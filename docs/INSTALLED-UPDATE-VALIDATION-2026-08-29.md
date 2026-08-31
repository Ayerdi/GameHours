# Installed Velopack validation — 2026-08-29

This record captures the real-Windows installed update smoke for the current WPF/Velopack path. It is manual product evidence, not a claim that the future signed GitHub-release pipeline has been exercised.

## Package provenance

The local persistent beta feed was built under:

`C:\Users\Alex\Downloads\GameHours-builds\velopack-update-smoke-20260829\source\artifacts\velopack\beta`

The first `0.2.0-beta.1` package had been produced from `a323758bfa9d1fd14a6087aa1f741a633dc99565`. Before continuing, the worktree was updated to `7b94537f1ed3e942d53d0a2174c1557f2e70b58e`, whose intervening product-independent changes fixed Windows PowerShell 5.1 package validation and restored normal draft CI gating. `0.2.0-beta.2` was built from that latter SHA.

The package chain was validated locally as:

- two full packages;
- one delta package for `0.2.0-beta.1 -> 0.2.0-beta.2`;
- valid `releases.beta.json`;
- unsigned Setup/package, as expected for this local smoke;
- no embedded production update source; the installed process used the explicit `GAMEHOURS_UPDATE_SOURCE` local-feed override.

CI #729 had already reproduced the package/validation path under Windows PowerShell 5.1 before the installed continuation.

## Installation over the existing app

The machine already had GameHours `0.1.1` installed under `%LOCALAPPDATA%\Ayerdi.GameHours`.

Running the local beta Setup correctly detected the existing installation and offered to update it to `0.2.0-beta.1`. After accepting:

- GameHours opened installed as `0.2.0-beta.1`;
- **Ajustes -> Actualizaciones** reported channel `Beta`;
- the pre-existing local library/history remained present (`8` games and approximately `64.9 h` at that point);
- `0.2.0-beta.2` was detected from the persistent local feed;
- the UI reported both the full package and **1 delta available**;
- `Ver novedades` showed the `0.2.0-beta.2` release notes from the package.

## Update while a game was active

`Slay the Spire 2` was launched and GameHours detected it as the active game. With the game still running, **Actualizar ahora** was pressed.

Observed behavior:

1. download progress reached `100%`;
2. GameHours closed by itself;
3. Velopack applied the update;
4. GameHours restarted by itself as `0.2.0-beta.2`;
5. the game remained running and was detected again after restart;
6. Settings reported `GameHours 0.2.0-beta.2 está actualizado` on channel `Beta`;
7. the local library/history remained present;
8. `Slay the Spire 2` appeared only once in the library, so the increase from 8 to 9 games was a legitimate newly tracked game rather than a duplicate;
9. the post-update release notes remained available from Settings.

## Session persistence across updater restart

The Activity/detail view showed the session that existed before the updater shutdown persisted as a finalized `24 s` **Salida de GameHours** session. Subsequent activity was recorded as new sessions after restart/normal game closure.

This is the intended boundary: the updater does not silently lose the pre-update session or fabricate one continuous interval across GameHours process replacement.

## Result

**Installed local Velopack update gate: VERIFIED.**

Verified on real Windows:

- installation over an older installed GameHours version;
- installed version/channel display;
- preservation of existing local data;
- local feed discovery through the explicit test override;
- release-note discovery;
- delta availability;
- in-app download/progress;
- graceful GameHours shutdown;
- automatic Velopack apply/restart;
- re-detection of an already-running game;
- persistence/segmentation of the active play session;
- post-update version/data/release notes.

Not covered by this smoke:

- Azure Artifact Signing / Authenticode;
- SmartScreen behavior for a signed build;
- actual public GitHub Releases upload/download from the installed client;
- uninstall/reinstall recovery.

Those remain separate gates and must not be inferred from this unsigned local validation.
