# Runtime performance validation — 2026-08-29

This record captures the expanded on-demand 30-second GameHours diagnostic on a real Windows machine after the installed updater path was verified. The comparison was intentionally performed in two stable states on the same machine: GameHours idle with no tracked game running, and GameHours while a detected game was running normally.

The purpose of this gate was to decide whether there was evidence for memory-retention profiling, GC tuning or other runtime optimization. It was not intended as a synthetic benchmark.

## Results

| Metric | Idle / no game | Tracked game running |
| --- | ---: | ---: |
| Duration | 30.0 s | 30.0 s |
| CPU average | 0.04% | 0.07% |
| Private memory average | 157.1 MiB | 156.9 MiB |
| Private memory peak | 158.0 MiB | 158.2 MiB |
| Working Set average | 182.8 MiB | 183.0 MiB |
| Working Set peak | 183.7 MiB | 184.5 MiB |
| Threads average | 23.9 | 23.0 |
| Threads peak | 24 | 24 |
| Reconciliations delta | +5 | +5 |
| Managed heap | 14.5 MiB | 12.4 MiB |
| Managed heap peak | 18.8 MiB | 18.8 MiB |
| Allocation rate | 0.18 MiB/s | 0.37 MiB/s |
| GC pause time | 0.01% | 0.01% |
| Gen0 collections | +1 | +1 |
| Gen1 collections | +1 | +1 |
| Gen2 collections | +0 | +0 |
| GC committed peak | 26.0 MiB | 26.0 MiB |
| GC fragmented peak | 0.5 MiB | 0.5 MiB |

## Interpretation

The tracked-game state did not materially increase GameHours process cost. CPU remained below 0.1% average in both samples. Private memory and Working Set were effectively flat across the comparison, with no evidence of growth in the managed heap or GC committed memory.

Allocation rate increased from 0.18 MiB/s to 0.37 MiB/s while a game was running, but the absolute rate remained low and did not translate into observable GC pressure: both samples recorded 0.01% pause time, one Gen0 collection, one Gen1 collection, no Gen2 collection, the same 26.0 MiB GC committed peak and the same 0.5 MiB fragmentation peak.

A 30-second comparison cannot prove the absence of every long-lived retention issue. It is sufficient for the current product decision because the measurement shows no anomaly that justifies speculative GC configuration, forced collections, pooling, retention work or memory-focused refactoring.

## Decision

**Runtime performance / memory gate: VERIFIED for the exercised idle and tracked-game states.**

No GC or memory optimization will be introduced from this evidence. Future optimization must start from a newly observed problem and measurement that identifies a real allocation, retention, latency or memory-growth issue.

The next distribution-level evidence gate remains the signed public path: provision Azure Artifact Signing/OIDC, execute the release workflow from `main`, validate Authenticode/SmartScreen behavior and then exercise a signed install/update/recovery cycle.
