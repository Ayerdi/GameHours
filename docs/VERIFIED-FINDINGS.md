# Verified findings

These are measurements from the feasibility probes and local Windows validation performed on 2026-08-20. They are design evidence, not universal Windows guarantees.

## .NET foundation validation

The first `feat/desktop-foundation` implementation was restored, built and tested on the target Windows machine with .NET SDK 8.0.424.

Observed result:

- restore completed for all six projects;
- Release build completed with **0 warnings / 0 errors**;
- **11/11 tests passed**;
- `GameHours.App` initialized `%LOCALAPPDATA%\GameHours\gamehours.db` and enumerated 279 visible processes.

## UserAssist

For the tested Gothic 1 Remake game executable, UserAssist v5 parsed as a 72-byte structure and produced approximately **12 h 34 min 41.765 s** of focus time.

The recorded last-run value was stale relative to known later play. `FocusCount` was non-zero while `RunCount` was zero, reinforcing that these fields must not be interpreted as simple launch counts.

Decision: UserAssist is secondary/corroborating historical evidence.

## SRUM

A safe copy of `SRUDB.dat` was parsed. `AppResourceUseInfo` rows for the actual game binary produced approximately **53 h 42 min 58.206 s** of foreground `FaceTime`, including activity on 2026-08-20.

This matched recalled playtime far better than UserAssist.

Decision: SRUM is the primary historical Windows source investigated so far, but its metric is foreground/focus time rather than exact process wall-clock runtime.

## Multiple same-name executables

The tested installation contained both a root/helper executable and the actual game binary with the same filename. The real binary lived under a `Binaries/Win64` path and dominated SRUM foreground time.

Decision: full path/game mapping outranks filename matching; helpers must not be blindly summed.

## Live monitoring

A live probe detected a session without launching the game through GameHours. In one test, WMI start/stop events were missed and one-second process reconciliation detected both boundaries.

Measured interval: **65.180 seconds**, classified `High` due to reconciliation.

Decision: periodic reconciliation remains part of the production design even when event-driven process exit observation is available. A future ETW collector may improve boundary precision, but reconciliation is permanent fallback coverage.
