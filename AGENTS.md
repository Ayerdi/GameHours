# AGENTS.md — GameHours

## Project

GameHours is a local-first Windows desktop application that measures and reconstructs videogame activity independently of launchers.

Stack: .NET 8, C#, WPF and SQLite.

GameHours must remain useful without an account, backend or Internet connection. External integrations are optional.

Before significant work, read:

- `docs/CONSTITUTION.md`
- `docs/ROADMAP.md`
- the active spec/plan for the task, if one exists.

Before repeating prior investigation, check `docs/VERIFIED-FINDINGS.md` and `docs/REFERENCE-PROJECTS.md`.

## Architecture

- `GameHours.Core`: neutral domain models and interfaces.
- `GameHours.Windows`: Windows discovery, monitoring and platform integration.
- `GameHours.Storage`: SQLite schema, migrations and repositories.
- `GameHours.Desktop`: WPF desktop product and composition.
- `GameHours.Portability`: backup, restore and import/export.
- `GameHours.AchievementProbe`: isolated achievement probing.
- `GameHours.Update`: update/package boundaries.
- `GameHours.Sync`: optional normalized integration contracts.

Do not introduce WPF, Windows, SQLite or backend dependencies into `GameHours.Core`.

## Commands

Restore:

`dotnet restore GameHours.sln --locked-mode`

Build:

`dotnet build GameHours.sln -c Release --no-restore`

Tests:

`dotnet test GameHours.sln -c Release --no-build`

Publish smoke:

`dotnet publish src/GameHours.Desktop/GameHours.Desktop.csproj -c Release -r win-x64 --self-contained true --no-restore -o artifacts/desktop-smoke`

## Conventions

Code and identifiers are in English. User-facing UI/messages are in Spanish unless a feature explicitly requires localization.

Reuse existing GameHours components, styles and abstractions before creating new ones.

## Rules

- Research before relevant technical, architectural, performance or UX decisions.
- Prefer the simplest correct solution; avoid speculative abstractions and dependencies.
- Fix root causes using evidence rather than layering patches.
- Never mix exact measured time with reconstructed historical estimates or double-count evidence.
- Never mutate the live SRUM database.
- Do not invent timestamps, achievements, metadata or identity.
- Keep GameHours functional offline; optional integrations must remain decoupled from authoritative tracking.
- Meaningful UI changes must respect the GameHours design and be visually verified on Windows when automation cannot prove the result.
- Do not merge, release, deploy or perform other irreversible external actions without explicit human authorization.

## When finishing a task

- Review the final diff for dead code, duplication, debug output, temporary logs and stale comments.
- Run validation proportional to the change; code PRs should pass build and relevant tests before being considered ready.
- Add regression tests where behavior could recur.
- Never weaken valid tests merely to obtain green CI.
- State clearly what is only implemented, what compiled, what passed automated tests/CI and what was manually or real-machine verified.
- If something could not be verified, say so.
