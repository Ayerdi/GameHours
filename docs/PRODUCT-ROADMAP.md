# GameHours product roadmap

This file records product work that is close enough to the current desktop foundation to influence implementation decisions. It deliberately separates **implemented**, **covered by automated tests** and **real-machine verified** states. GitHub-hosted Windows jobs are operational again; automatically verified status still belongs to a specific green HEAD and must be checked on the PR before merge.

## Runtime trust and attention

| Capability | Implementation | Automated coverage | Real Windows validation |
| --- | --- | --- | --- |
| Executed playtime remains authoritative | Implemented | Existing tests | Partially validated |
| Focused playtime | Implemented | Policy/persistence tests present | Pending |
| Estimated active playtime | Implemented | Policy/persistence tests present | Pending |
| Configurable AFK: off / 2 / 5 / 10 / 15 min | Implemented | Tests present | Pending |
| AFK disabled skips idle-input APIs | Implemented | Code boundary covered | Pending |
| AFK disabled never fabricates active duration | Implemented; focus-only rows keep active at zero | Policy/repository/schema tests present | Pending |
| XInput uses packet-change only | Implemented | Privacy-boundary test present | Pending |
| Activity provenance per session | Implemented in schema v5 | Migration/repository tests present | Pending |
| Focus/active/session-telemetry statistics | Implemented | Integration coverage present | Pending UI pass |
| Deferred AFK preference exposes configured vs applied state | Implemented in Diagnostics | Tests present; required PR CI | Pending |

## Low-impact runtime

| Capability | Implementation | Automated coverage | Real Windows validation |
| --- | --- | --- | --- |
| Event-driven process starts | Implemented | Policy tests present | Pending measurement |
| Five-second safety reconciliation | Implemented | Policy tests present | Pending measurement |
| One-second degraded fallback | Implemented | Policy tests present | Pending forced-failure test |
| Clear degraded mode when monitor stops | Implemented | Tests present; required PR CI | Pending lifecycle check |
| Event-driven achievement files | Implemented | Watcher tests present | Pending measurement |
| Low-impact mode | Implemented, default on | Tests present; required PR CI | Pending measurement |
| Defer read-model refresh while playing | Implemented | Tests present; required PR CI | Pending |
| Release pending refresh when low-impact is disabled | Implemented | Tests present; required PR CI | Pending |
| Pause recurring update timer while playing | Implemented | Tests present; required PR CI | Pending |
| On-demand runtime diagnostics | Implemented | Passive baseline test present | Pending visual/runtime check |

## Transparency and control

| Capability | Status |
| --- | --- |
| Explain what AFK observation reads and does not store | Implemented in Settings/Diagnostics |
| Show configured vs currently applied AFK policy | Implemented in Diagnostics |
| Show event-driven vs degraded process tracking | Implemented in Diagnostics |
| Show passive event/reconciliation counters | Implemented in Diagnostics |
| Show on-demand GameHours memory/CPU/thread snapshot | Implemented; no diagnostic timer |
| Manual executable classification/ignore/association | Already implemented in `Pendientes`; reused from Settings |
| Data-quality presentation | Implemented in Statistics: share of measured sessions with telemetry plus measured-vs-known data share; no per-second coverage claim |

## Save-game intelligence — exploratory

A future product line will investigate whether GameHours can use its knowledge of real game sessions to **discover and protect save-game data without becoming a launcher or a general-purpose filesystem monitor**.

The current direction is deliberately hybrid:

- GameHours owns discovery and confidence/provenance;
- the Ludusavi Manifest / PCGamingWiki-derived dataset is the primary known-location source;
- Steam/Epic/GOG identity and install metadata help resolve paths safely;
- engine conventions (initially Unity, Unreal and Godot) are candidates for measured fallback experiments, not authorities;
- session-bounded filesystem changes may be explored only inside plausible roots, with no global continuous scan;
- Ludusavi may later be an **optional** advanced backup/restore backend through its CLI/API, never a dependency for GameHours tracking or save discovery;
- restore is intentionally later and stricter than backup.

This work is **not authorized by the current foundation execution plan**. It must start with read-only experiments and measurable false-positive/runtime gates before persistence, backup automation or UI complexity is introduced.

Detailed research plan, architecture direction, safety rules, experiments and acceptance gates: [`SAVE-GAME-ROADMAP.md`](SAVE-GAME-ROADMAP.md).

## Pending real-machine gate

The following remain intentionally open until a Windows test machine is available:

1. configurable AFK behavior with keyboard/mouse and XInput controller;
2. disabled-AFK focus-only behavior and unavailable active estimate;
3. deferred AFK preference lifecycle and Diagnostics configured/applied presentation;
4. low-impact resource comparison plus immediate release of pending refreshes when low-impact is disabled;
5. WMI degraded fallback, stopped-state cleanup and missed-event recovery;
6. achievement watcher/fallback behavior;
7. UI/UX pass for Settings, Diagnostics and the new Statistics cards;
8. portable import/restore lifecycle;
9. packaged Velopack update flow;
10. launcher/process-family edge cases and additional achievement-source variants.

The canonical executable checklist is `docs/REAL-MACHINE-VALIDATION.md`.

## Decision rule

A roadmap item is not considered complete merely because code exists. GameHours should describe it as:

- **implemented** when the code and migrations exist;
- **covered by automated tests** when relevant tests exist but have not necessarily run on the current HEAD;
- **automatically verified** only after the relevant build/tests pass on that HEAD;
- **real-machine verified** only after the corresponding hardware checklist has actually been exercised.
