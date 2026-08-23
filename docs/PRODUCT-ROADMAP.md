# GameHours product roadmap

This file records product work that is close enough to the current desktop foundation to influence implementation decisions. It deliberately separates **implemented**, **covered by automated tests** and **real-machine verified** states. The current roadmap batch still needs a normal CI run because GitHub-hosted jobs are failing before their first step.

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
| Deferred AFK preference exposes configured vs applied state | Implemented in Diagnostics | Final CI pending | Pending |

## Low-impact runtime

| Capability | Implementation | Automated coverage | Real Windows validation |
| --- | --- | --- | --- |
| Event-driven process starts | Implemented | Policy tests present | Pending measurement |
| Five-second safety reconciliation | Implemented | Policy tests present | Pending measurement |
| One-second degraded fallback | Implemented | Policy tests present | Pending forced-failure test |
| Clear degraded mode when monitor stops | Implemented | Final CI pending | Pending lifecycle check |
| Event-driven achievement files | Implemented | Watcher tests present | Pending measurement |
| Low-impact mode | Implemented, default on | Final CI pending | Pending measurement |
| Defer read-model refresh while playing | Implemented | Final CI pending | Pending |
| Release pending refresh when low-impact is disabled | Implemented | Final CI pending | Pending |
| Pause recurring update timer while playing | Implemented | Final CI pending | Pending |
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
