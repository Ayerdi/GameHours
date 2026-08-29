# Real-machine validation backlog

GameHours deliberately separates **implemented / automated-verified** from **verified on a real Windows installation**. This file tracks only hardware/installed-app evidence that is still useful after the 2026-08-28 foundation pass.

The detailed evidence already collected is recorded in [`FOUNDATION-VALIDATION-2026-08-28.md`](FOUNDATION-VALIDATION-2026-08-28.md) and [`INSTALLED-UPDATE-VALIDATION-2026-08-29.md`](INSTALLED-UPDATE-VALIDATION-2026-08-29.md). Do not reopen completed checks merely because an older checklist once contained them.

## Preflight for a new manual pass

Before marking a new item complete:

1. record the exact branch SHA from `git rev-parse HEAD`;
2. confirm **Build, test and package (.NET 8 / Windows)** is green for that SHA;
3. record the installed GameHours version/channel and relevant settings;
4. preserve/backup user data before destructive recovery tests;
5. record the observed result — CI is supporting evidence, not a replacement for the installed-machine check.

## Foundation evidence already closed

The 2026-08-28 pass already established enough real-machine evidence to close the user-selected foundation gate:

- [x] normal desktop startup/navigation and candidate workflow;
- [x] first-open `Pendientes` responsiveness after moving SQLite work off the WPF dispatcher;
- [x] Split Fiction recognition after the known-install/runtime-evidence fix;
- [x] desktop backup / restore / portable import smoke;
- [x] SRUM source discovery and identity cleanup for Google Play Games host infrastructure, Palworld and architecture-folder fallbacks;
- [x] 30-second runtime baseline in idle and while a tracked game was running.

The measured baseline was:

| State | CPU avg | Private memory avg / peak | Working set avg / peak | Threads avg / peak | Reconciliations |
| --- | ---: | ---: | ---: | ---: | ---: |
| Idle | 0.07% | 154.9 / 155.2 MiB | 215.7 / 216.1 MiB | 23.3 / 24 | +6 / 30 s |
| Game running | 0.09% | 157.4 / 158.1 MiB | 220.5 / 221.2 MiB | 27.6 / 28 | +4 / 30 s |

Those figures do **not** justify speculative GC or memory tuning.

## Suspend/resume product decision

A hardware suspend/resume exercise is **not required for the current foundation/release work by explicit product decision**.

The path remains protected by automated tests and implementation boundaries, including the post-resume `ResumedAtUtc` lower bound. It must not be described as real-machine verified. If suspend/resume becomes a product priority later, a dedicated hardware gate can be reintroduced then.

## Current runtime/memory measurement gate

The diagnostic 30-second sampler now exposes more useful managed-runtime evidence without forcing collections or adding a permanent monitoring loop.

When convenient on the current green build, collect the same-duration sample in at least these two stable states:

- [ ] GameHours idle;
- [ ] tracked game running.

Record:

| State | CPU | Private / WS | Managed heap avg / peak | Allocation rate | GC committed / fragmented | GC pause % | Gen0 / Gen1 / Gen2 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Idle |  |  |  |  |  |  |  |
| Game running |  |  |  |  |  |  |  |

Only if these values show a real problem should the next step be a `gcdump`/retention investigation. Do not add `GC.Collect()`, working-set trimming, Server GC or cache churn merely to lower a screenshot number.

## Installed Velopack gate — verified 2026-08-29

The installed WPF update path is now **VERIFIED** on real Windows. Exact evidence is recorded in [`INSTALLED-UPDATE-VALIDATION-2026-08-29.md`](INSTALLED-UPDATE-VALIDATION-2026-08-29.md).

- [x] build/install `0.2.0-beta.1` from the current Desktop package path;
- [x] run it against the persistent local beta feed through the explicit `GAMEHOURS_UPDATE_SOURCE` override;
- [x] confirm **Ajustes → Actualizaciones** shows installed version `0.2.0-beta.1` and channel `Beta`;
- [x] confirm existing games/sessions/data remain present;
- [x] package `0.2.0-beta.2` into the same feed with different release notes;
- [x] confirm `Buscar actualizaciones` offers `beta.2` and `Ver novedades` shows its notes;
- [x] press `Actualizar ahora`, observe download/progress and graceful exit;
- [x] confirm Velopack restarts GameHours as `beta.2`;
- [x] confirm `%LOCALAPPDATA%\GameHours\gamehours.db` remains intact;
- [x] confirm post-update `Novedades` remains available from Settings;
- [x] confirm an active pre-update game is detected again after restart and the pre-update session is finalized instead of lost;
- [x] confirm the newly tracked game is not duplicated in the library.

This local unsigned smoke validates the installed updater experience. It does **not** satisfy the signed-public-release gate below.

## Signed-release gate — later

The repository is prepared for Azure Artifact Signing + GitHub OIDC, but those external resources are not provisioned/verified yet. Once they exist:

- [ ] run the main-only **Package Windows** workflow successfully;
- [ ] confirm the Setup and packaged GameHours executables have valid Authenticode signatures;
- [ ] confirm the GitHub artifact attestation exists for the checksummed output;
- [ ] install/update the signed build on Windows;
- [ ] evaluate SmartScreen behavior with the signed binary.

Do not use an unsigned local smoke as evidence for the signed-release gate.

## Recovery / uninstall gate — later

GameHours intentionally does not enable routine feed-driven downgrades. Normal bad-release recovery is a higher-version signed hotfix.

For a controlled recovery exercise after signed packaging exists:

- [ ] create a consistent backup first;
- [ ] uninstall/reinstall a known-good signed build;
- [ ] confirm the Velopack application directory is replaced/removed as expected;
- [ ] confirm `%LOCALAPPDATA%\GameHours` and the database survive because they are outside the `Ayerdi.GameHours` install root;
- [ ] confirm the reinstalled application opens the preserved data normally.

## Optional compatibility backlog

These remain useful regression coverage when matching software/hardware happens to be available, but they do not block the already-closed practical foundation gate:

- launcher remains alive while the real game starts;
- launcher exits before the real game child is fully observed;
- helper/anti-cheat starts before the real game and is not counted as gameplay;
- another compatible GSE/Goldberg achievement layout;
- additional Steam local-stats/cache variants;
- controller-only AFK/activity behavior;
- Windows lock-session attention behavior.

Any mismatch found in these paths should reopen the specific behavior, not the whole foundation.

## Recording a validation

When a pending item is actually exercised:

1. mark it `[x]` only when observed behavior matches the intended contract;
2. record version/SHA/environment in the accompanying evidence note or PR;
3. keep failures unchecked and fix the root cause;
4. rerun relevant CI after code changes;
5. never convert automated coverage into a manual-verification claim.
