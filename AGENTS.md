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

## Efficient subagent policy

Use the project-scoped agents in `.codex/agents/` automatically when their role matches the work. The goal is to reduce primary-context pollution and total cost, not to maximize agent count.

- Keep the primary agent on `gpt-5.6-sol` with `medium` reasoning for requirements, architecture, integration, external actions and the final decision.
- Use `gamehours_mapper` for bounded read-only codebase questions before expensive exploration in the primary thread.
- Use `gamehours_worker` for a clearly owned implementation slice and `gamehours_storage_worker` for SQLite, migrations, restore or portability. Never assign overlapping file ownership to concurrent workers.
- Use `gamehours_test_runner` for lengthy or independent local validation and failure reproduction.
- Use `gamehours_supervisor` after non-trivial or high-risk implementation involving architecture, persistent data, concurrency, security or broad diffs. It reviews; it does not reimplement the worker's task.
- Always delegate GitHub Actions, PR checks and CI observation to `ci_monitor`. The primary agent retains rerun, merge, cancellation, deployment and rollback decisions.
- Choose the cheapest capable role. Do not spawn every agent mechanically, do not delegate trivial one- or two-step work, and do not duplicate the same investigation in multiple agents.
- Run independent read-heavy tasks in parallel when useful. Serialize write-heavy tasks that touch related files.
- Give every worker a concrete objective, explicit file ownership, constraints, expected evidence and a reminder that other agents may be editing the shared worktree.
- Spawn custom project agents with `fork_turns="none"` and pass a compact, self-contained briefing. Do not copy the full parent history unless a task demonstrably requires it.
- The primary agent reviews and integrates all worker output, runs proportionate final validation, and remains responsible for the final diff.

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
