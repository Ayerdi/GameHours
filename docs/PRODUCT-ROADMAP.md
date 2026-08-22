# GameHours product roadmap

This file records product work that is close enough to the current desktop foundation to influence implementation decisions. It deliberately separates **implemented**, **automatically verified** and **real-machine verified** states.

## Runtime trust and attention

| Capability | Implementation | Automated verification | Real Windows validation |
| --- | --- | --- | --- |
| Executed playtime remains authoritative | Implemented | Existing tests | Partially validated |
| Focused playtime | Implemented | Policy/persistence covered | Pending |
| Estimated active playtime | Implemented | Policy/persistence covered | Pending |
| Configurable AFK: off / 2 / 5 / 10 / 15 min | Implemented | Tests added | Pending |
| AFK disabled skips idle-input APIs | Implemented | Code boundary covered | Pending |
| XInput uses packet-change only | Implemented | Privacy-boundary test | Pending |
| Activity provenance per session | Implemented in schema v5 | Migration/repository tests added | Pending |
| Focus/active/coverage statistics | Implemented | Integration coverage added | Pending UI pass |

## Low-impact runtime

| Capability | Implementation | Automated verification | Real Windows validation |
| --- | --- | --- | --- |
| Event-driven process starts | Implemented | Policy tests | Pending measurement |
| Five-second safety reconciliation | Implemented | Policy tests | Pending measurement |
| One-second degraded fallback | Implemented | Policy tests | Pending forced-failure test |
| Event-driven achievement files | Implemented | Watcher tests | Pending measurement |
| Low-impact mode | Implemented, default on | Logic/compile validation pending final CI | Pending measurement |
| Defer read-model refresh while playing | Implemented | Final CI pending | Pending |
| Pause recurring update timer while playing | Implemented | Final CI pending | Pending |
| On-demand runtime diagnostics | Implemented | Passive baseline test added | Pending visual/runtime check |

## Transparency and control

| Capability | Status |
| --- | --- |
| Explain what AFK observation reads and does not store | Implemented in Settings/Diagnostics |
| Show event-driven vs degraded process tracking | Implemented in Diagnostics |
| Show passive event/reconciliation counters | Implemented in Diagnostics |
| Show on-demand GameHours memory/CPU/thread snapshot | Implemented; no diagnostic timer |
| Manual executable classification/ignore/association | Already implemented in `Pendientes`; now reused from Settings |
| Data-quality presentation | Implemented in Statistics: telemetry coverage and measured-vs-known share |

## Pending real-machine gate

The following remain intentionally open until a Windows test machine is available:

1. configurable AFK behavior with keyboard/mouse and XInput controller;
2. disabled-AFK focus-only behavior;
3. low-impact resource comparison (CPU, memory, disk and full-snapshot cadence);
4. WMI degraded fallback and missed-event recovery;
5. achievement watcher/fallback behavior;
6. UI/UX pass for Settings, Diagnostics and the new Statistics cards;
7. portable import/restore lifecycle;
8. packaged Velopack update flow;
9. launcher/process-family edge cases and additional achievement-source variants.

The canonical executable checklist is `docs/REAL-MACHINE-VALIDATION.md`.

## Decision rule

A roadmap item is not considered complete merely because code exists. GameHours should describe it as:

- **implemented** when the code and migrations exist;
- **automatically verified** only after the relevant build/tests pass;
- **real-machine verified** only after the corresponding hardware checklist has actually been exercised.
