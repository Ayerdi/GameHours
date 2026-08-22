# Real-machine validation backlog

GameHours deliberately separates **implemented/tested in CI** from **verified on a real Windows installation**.

This checklist is the canonical backlog for hardware/installed-app validation that can be deferred while implementation continues. A CI-green feature must not be described as real-machine verified until the corresponding item here has actually been exercised.

## Already confirmed on a second Windows PC

- [x] desktop startup and normal navigation;
- [x] clean SQLite initialization;
- [x] Steam installed-game discovery for Slay the Spire 2;
- [x] active-game detection and measured session persistence;
- [x] measured sessions appearing in Library / Calendar / game activity;
- [x] candidate-noise cleanup;
- [x] Balatro achievement catalogue, unlock state, progress, unlock time, title, description and icons;
- [x] startup responsiveness regression reproduced and fixed (published EXE no longer has the multi-second post-show input freeze).

## Deferred portability and recovery validation

Run these together when a spare/second Windows installation is available.

- [ ] create a full backup from **Ajustes** while GameHours has normal local data;
- [ ] restore that backup and confirm games, measured time, historical time, achievements and local decisions remain intact;
- [ ] confirm a `pre-restore-*.db` safety backup is created before replacement;
- [ ] export portable JSON v1 from installation/database A;
- [ ] import that JSON into a clean installation/database B;
- [ ] compare games, measured sessions, historical evidence and achievement state between A and B;
- [ ] confirm machine-specific executable paths/candidates were not transferred by the portable import;
- [ ] import the same JSON a second time and confirm it is idempotent (duplicates reported, no added playtime);
- [ ] create a controlled UUID/data conflict and confirm preview blocks the whole import without modifying data;
- [ ] create/choose a controlled timeline overlap and confirm preview blocks it without modifying data;
- [ ] start a tracked game, begin portable import, and confirm GameHours finalizes the active session, revalidates, imports safely and resumes tracking;
- [ ] after import, confirm Library / Activity / Calendar / Statistics refresh to the imported state.

## Deferred packaged installer/update validation

The core Velopack mechanism has previously been validated with the older development host. The current WPF Desktop package path still needs one final installed-app pass.

- [ ] generate a validated Windows package using the manual **Package Windows** workflow or `scripts/package-windows.ps1`;
- [ ] install the generated Setup executable on a clean Windows profile;
- [ ] confirm GameHours launches from the installed shortcut and tray behavior is normal;
- [ ] confirm `%LOCALAPPDATA%\GameHours\gamehours.db` stays outside the Velopack install directory;
- [ ] confirm **Ajustes → Actualizaciones** shows the installed version and expected channel;
- [ ] package a newer version against a persistent local/test feed and confirm update detection;
- [ ] confirm the tray update notification appears only once for the target version;
- [ ] confirm release notes render in `Novedades`;
- [ ] download/apply the update while a game is active and confirm the session is finalized before exit;
- [ ] confirm Velopack restarts GameHours on the new version and the existing database remains intact;
- [ ] confirm `Novedades` is shown once after the update and remains manually accessible later.

## Deferred launcher/process-family edge cases

- [ ] launcher remains alive while the real game starts;
- [ ] launcher exits before the real game child is fully observed;
- [ ] helper/anti-cheat starts before the real game and is not counted as gameplay;
- [ ] multiprocess game does not double-count overlapping related processes;
- [ ] PID reuse does not incorrectly connect an unrelated process to a recently exited launcher;
- [ ] suspend/resume while a launcher family is active does not count sleep time.

## Deferred achievement-source variants

- [ ] another GSE/Goldberg layout beyond the already validated Project P.I.T.T./Balatro cases;
- [ ] Steam local-stats/cache variants that are available on the test machine;
- [ ] partial/unlock-only source does not masquerade as a complete catalogue;
- [ ] conflicting compatible-emulator and official-Steam data remain isolated as designed;
- [ ] 100% completion milestone remains stable after restart/re-observation.

## Recording a validation

When an item is actually tested:

1. mark it `[x]` only if the observed result matches the intended behavior;
2. record any relevant title/version/environment in the PR or commit that marks it complete;
3. if behavior differs, keep the item unchecked and fix the cause before claiming verification;
4. rerun the relevant CI after any code fix even when the original problem was hardware-specific.

This document is intentionally allowed to contain pending items when the foundation PR is otherwise progressing. Pending hardware validation is not a reason to add speculative code or weaken automated tests.
