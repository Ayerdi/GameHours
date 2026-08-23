# Real-machine validation backlog

GameHours deliberately separates **implemented/covered by automated tests** from **verified on a real Windows installation**.

This checklist is the canonical backlog for hardware/installed-app validation that can be deferred while implementation continues. A feature must not be described as real-machine verified until the corresponding item here has actually been exercised. The current roadmap batch also remains pending a normal CI run while GitHub-hosted runners fail before their first step.

## Already confirmed on a second Windows PC

- [x] desktop startup and normal navigation;
- [x] clean SQLite initialization;
- [x] Steam installed-game discovery for Slay the Spire 2;
- [x] active-game detection and measured session persistence;
- [x] measured sessions appearing in Library / Calendar / game activity;
- [x] candidate-noise cleanup;
- [x] Balatro achievement catalogue, unlock state, progress, unlock time, title, description and icons;
- [x] startup responsiveness regression reproduced and fixed (published EXE no longer has the multi-second post-show input freeze).

## Deferred focused / active playtime validation

Automated tests cover the schema/persistence rules and pure activity policy, but this batch is not currently CI-verified and the Windows signals themselves still need to be exercised on hardware.

- [ ] keep a tracked game focused with keyboard/mouse interaction and confirm executed, focused and active time increase together;
- [ ] Alt+Tab to another application while leaving the game running and confirm only executed time continues increasing;
- [ ] return focus to the game and confirm focused time resumes;
- [ ] test the 2, 5, 10 and 15 minute AFK choices and confirm active time stops at the selected cutoff while focused time continues;
- [ ] resume keyboard/mouse input after AFK and confirm active time resumes without altering the authoritative measured session;
- [ ] set AFK to **Disabled** and confirm focused time continues to work, estimated active is shown as unavailable, and keyboard/mouse idle plus XInput activity are not queried by the provider;
- [ ] change the AFK preference during an active session and confirm the current session keeps its original policy, Diagnóstico shows configured vs applied values while they differ, and the new policy applies after the session finishes;
- [ ] repeat the active/idle test using an XInput-compatible controller with no keyboard/mouse input;
- [ ] confirm a controller input that occurs between sampling ticks is still detected through the XInput packet change;
- [ ] verify a multiprocess game counts focus when the foreground window belongs to another PID already mapped to the same active game;
- [ ] lock the Windows session while a game remains open and confirm locked time is not counted as focused/active;
- [ ] suspend/resume Windows while a game is active and confirm the sampling gap is not fabricated as focused/active time;
- [ ] confirm sessions created before activity telemetry show focused/active as unavailable rather than zero;
- [ ] confirm lifetime statistics exclude AFK-disabled sessions from **active estimated** while still including their focused-time data;
- [ ] confirm the detail/statistics views clearly distinguish executed, focused, estimated active and the share of measured sessions that actually have telemetría.

## Deferred runtime-efficiency validation

The event-driven paths and fallback policies have automated coverage, but the current batch still needs a normal CI run and their real impact/Windows behavior must be measured before claiming a performance win on hardware.

- [ ] record GameHours CPU, working-set memory and disk activity for several minutes while no game is running;
- [ ] repeat while a tracked game is running and compare GameHours overhead with the previous one-second full-reconciliation build;
- [ ] open **Ajustes → Diagnóstico** and confirm its process mode, event count and reconciliation count match observed behavior without creating an additional periodic sampling loop;
- [ ] stop/restart tracking through a normal lifecycle and confirm Diagnóstico reports **Monitor detenido** rather than retaining a stale fallback mode;
- [ ] confirm normal process starts are received immediately through the WMI event path without waiting for the five-second safety reconciliation;
- [ ] confirm a deliberately missed/unavailable WMI path is recovered by reconciliation without losing the measured game start;
- [ ] confirm WMI unavailability makes the monitor fall back to one-second reconciliation rather than stopping tracking;
- [ ] verify complete process snapshots occur roughly every five seconds while WMI is healthy, not every second;
- [ ] enable **Impacto mínimo al jugar**, unlock an achievement and confirm tracking/achievement persistence still work while automatic library refresh is deferred until gameplay ends;
- [ ] while a deferred nonessential refresh is pending, disable **Impacto mínimo** during the active game and confirm that refresh is released immediately instead of waiting for gameplay to end;
- [ ] confirm the six-hour update timer is stopped while a game is active in low-impact mode and resumes after gameplay;
- [ ] unlock an achievement whose state file is already known and confirm the exact-file watcher observes it promptly without one-second file polling;
- [ ] verify unrelated writes in the same achievement directory do not trigger achievement re-reads;
- [ ] leave an achievement state file unchanged for over 30 seconds and confirm the low-frequency fallback remains functional;
- [ ] suspend/resume with a tracked game active and confirm the independent one-second uptime sampling still prevents sleep time from entering the session.

See `docs/RUNTIME-EFFICIENCY.md` for the runtime-observation policy and intended fallbacks.

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
