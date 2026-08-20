# AGENTS.md — maintaining GameHours

## Goal

GameHours measures and reconstructs Windows game playtime independently of launchers. It is local-first and is intended to become the tracking subsystem of the Gestor de Juegos desktop application without coupling the tracking core to that backend.

## Non-negotiable design rules

1. **Exact and reconstructed time stay distinguishable.** Never label SRUM/UserAssist evidence as exact process runtime.
2. **No double counting.** Baseline evidence ends at the tracking cutover. Gap recovery must not overlap measured sessions.
3. **Path outranks filename.** Two executables with the same filename may belong to different roles or games.
4. **Helpers are not game time by default.** Launchers, crash reporters and helper processes need explicit resolution/grouping rules.
5. **Local-first.** Tracking and persistence work without network access.
6. **Privacy-minimal sync.** Raw SRUM, registry values, PIDs, Windows usernames and full paths are not uploaded by default.
7. **Idempotent persistence/sync.** Client-generated UUIDs identify sessions/evidence so retries cannot duplicate time.
8. **Events are not enough.** The production monitor keeps periodic reconciliation as a fallback for missed process events.
9. **No silent data repair.** Never repair or mutate the live SRUM database. Read from safe copies/imports only.
10. **Tests accompany timeline changes.** Any change to cutover, overlap or duration rules requires focused tests.

## Projects

- `GameHours.Core`: domain models, timeline policy and interfaces. No Windows/SQLite/backend dependencies.
- `GameHours.Windows`: Windows-specific discovery and monitoring.
- `GameHours.Storage`: SQLite schema and repositories.
- `GameHours.Sync`: normalized sync contracts/client boundary.
- `GameHours.App`: development host now; future desktop shell.
- `tests/GameHours.Tests`: unit/integration tests using temporary SQLite databases.

## Commands

```powershell
dotnet restore GameHours.sln
dotnet build GameHours.sln -c Release
dotnet test GameHours.sln -c Release
```

## Verified design state

As of 2026-08-20:

- SRUM `AppResourceUseInfo.FaceTime` was successfully extracted from a copied SRUDB and matched the user's recalled playtime much better than UserAssist for the test game.
- UserAssist v5 focus fields parsed structurally, but the last-run value became stale and therefore it is secondary evidence.
- A live process session was detected entirely through one-second reconciliation when WMI events were missed; measured duration was 65.180 seconds.
- The tested game exposed two executable paths with the same filename (helper/root executable and the real game binary), proving that filename-only identity is insufficient.

See `docs/VERIFIED-FINDINGS.md` for details.

## Do not assume

- SRUM foreground time equals process lifetime.
- `FocusCount` in UserAssist equals launch count.
- a process event will always arrive.
- one executable filename uniquely identifies a game.
- a Steam counter and GameHours counter can safely be added.
- backend availability during play.

## Pull-request checklist

- `dotnet build GameHours.sln -c Release`
- `dotnet test GameHours.sln -c Release`
- no machine-specific paths, usernames or secrets committed;
- timeline rules unchanged or explicitly tested/documented;
- SQLite migrations remain forward-only and additive where practical.
