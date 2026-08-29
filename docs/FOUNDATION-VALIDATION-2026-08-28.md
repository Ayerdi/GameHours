# Foundation real-machine validation — 2026-08-28

This record captures the Windows validation performed against the `feat/desktop-foundation` line after the deterministic foundation batches were completed. It complements `REAL-MACHINE-VALIDATION.md`; it does not turn an untested hardware path into a verified one.

## Practical foundation result

The user-selected practical foundation gate is closed for the behavior exercised below. Suspend/resume remains automatically covered but was deliberately not exercised on hardware because it is not a product behavior the user wants to spend manual validation time on.

The later installed Velopack gate was exercised separately on 2026-08-29 and is recorded in [`INSTALLED-UPDATE-VALIDATION-2026-08-29.md`](INSTALLED-UPDATE-VALIDATION-2026-08-29.md).

## UI and candidate detection

A real Windows pass exposed two regressions that were fixed before closure:

- the first opening of **Pendientes** could render white and feel blocked because SQLite initialization happened on the WPF dispatcher; the initialization/read path was moved off the UI thread and the user confirmed the first opening became responsive;
- `SplitFiction.exe` could remain at 70% because its one-shot initial resolution happened before a visible main window existed. Known installed-game path + graphics runtime + non-helper role is now enough strong evidence without adding polling, and the user confirmed Split Fiction is recognized normally.

Stale pending candidates are also closed when an executable has subsequently been learned as a trackable mapping.

## Portability

The user exercised the desktop backup/restore/import flow on the real machine and confirmed it behaved correctly. Automated tests remain the source of truth for controlled conflict/idempotency cases and restore rollback invariants.

The portability implementation additionally rejects unrelated SQLite databases before touching the live database, uses a GameHours `application_id`, keeps a legacy compatibility fingerprint for old backups, stages restores before replacement, and preserves a safety backup.

## Runtime baseline

The built-in 30-second diagnostic measurement was run in two states on the same machine.

| State | Duration | CPU avg | Private memory avg / peak | Working set avg / peak | Threads avg / peak | Reconciliations delta |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Idle / GameHours at rest | 30.0 s | 0.07% | 154.9 / 155.2 MiB | 215.7 / 216.1 MiB | 23.3 / 24 | +6 |
| Tracked game running | 30.0 s | 0.09% | 157.4 / 158.1 MiB | 220.5 / 221.2 MiB | 27.6 / 28 | +4 |

Observed delta while a game was running was approximately +0.02 percentage points CPU, +2.5 MiB average private memory, +4.8 MiB average working set and +4.3 average threads.

This evidence does **not** justify speculative memory or GC tuning. Any future memory optimization must still start with GC heap/allocation/retention measurement rather than Working Set alone.

## SRUM recovery

The real-machine SRUM pass was iterated until the remaining false identities were removed without broadening game detection indiscriminately.

Observed and fixed:

- `C:\Program Files\Google\Play Games\current\emulator\crosvm.exe` was initially interpreted as Google Play Games historical activity; `crosvm.exe` is platform virtualization infrastructure and is now rejected before stale mappings/GameConfigStore can promote it;
- another host process under `C:\Program Files\Google\Play Games\...` still produced a 39 h 48 min candidate; executable paths inside the Google Play Games host tree are now treated as platform infrastructure, not Android games;
- Palworld's historical path under `...\steamapps\common\Palworld\Pal\Binaries\Win64\Palworld-Win64-Shipping.exe` originally surfaced as `Win64`; historical Steam paths now prefer the `steamapps\common\<install-folder>` identity;
- Rocket League's historical `...\steamapps\common\rocketleague\Binaries\Win64\RocketLeague.exe` likewise no longer surfaces as `Win64`; the final real-machine screenshot showed `rocketleague`;
- the SRUM UI now exposes the complete application path as a tooltip so future identity anomalies can be diagnosed without relying on truncated text.

Final real-machine result:

- Google Play Games host candidates absent;
- Palworld displayed as Palworld;
- the former generic Win64 row displayed as the Steam install identity (`rocketleague`);
- previously imported historical candidates remained marked imported.

`rocketleague` casing/display polish is not treated as an SRUM identity defect and does not block foundation closure.

## Suspend/resume

A real suspend/resume exercise was **not performed** by explicit product decision. The deterministic protection remains in the code and automated tests: a surviving process is reopened no earlier than `ResumedAtUtc`, and the sleep interval is not reconstructed into measured playtime.

Do not describe suspend/resume as real-machine verified. If it becomes a product priority later, restore the hardware gate from `REAL-MACHINE-VALIDATION.md`.

## Release-level state after the foundation pass

The previously pending installed Velopack smoke is now verified separately: installation over an older version, data preservation, `beta.1 -> beta.2` delta discovery, in-app download, graceful exit, automatic restart and active-session persistence all passed on real Windows.

The remaining release-level gates are specifically the **signed/public** path and recovery exercise: Azure Artifact Signing/OIDC, Authenticode, GitHub Releases publication, SmartScreen evaluation and controlled uninstall/reinstall recovery. They must not be inferred from the unsigned local update smoke.
