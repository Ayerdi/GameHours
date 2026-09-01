# GameHours Constitution

These principles are non-negotiable. Every specification, plan, implementation and review must respect them.

## 1. Research before deciding

Understand the existing behavior and code before changing it. For relevant technical, architectural, performance or UX decisions, check whether GameHours, .NET/WPF/Windows or an established solution already solves the problem. Prefer official documentation and reliable primary sources; inspect mature open-source implementations when they add useful evidence.

Do not implement the first plausible solution merely because it works.

## 2. Simplicity and reuse first

Prefer the smallest clear solution that correctly solves the real problem. Reuse existing GameHours components and platform capabilities before adding code, abstractions or dependencies.

Avoid duplicate logic, speculative frameworks, unnecessary state, hidden side effects and broad refactors unrelated to the task. Fewer lines are only better when clarity and maintainability are preserved.

## 3. Evidence and root cause over assumptions

For bugs or unexpected behavior: characterize the failure, gather evidence, identify the owning layer and fix the root cause there. Logs, tests, metrics, runtime behavior and the actual code outrank hypotheses or stale documentation.

Do not accumulate patches around a structural problem. If evidence disproves an earlier assumption, discard the assumption.

## 4. Preserve data truth and provenance

GameHours must never fabricate precision.

Measured runtime, reconstructed historical evidence, focused/active telemetry, achievements, timestamps and external metadata must preserve their source and confidence. Never double-count time, present estimated evidence as exact, invent unlock times or silently overwrite authoritative local history with external snapshots.

The live SRUM database is read-only evidence: never repair or mutate it. Use safe copies/imports.

## 5. Local-first and optional integrations

Core tracking, persistence and the useful desktop experience must work without an account, backend or Internet connection.

External systems such as Gestor de Juegos, online metadata providers or save engines are optional adapters behind GameHours-owned boundaries. They may enrich the product but must not become runtime dependencies of authoritative tracking or replace GameHours identities and evidence.

Privacy follows the same rule: collect, persist and transmit only what the feature actually needs.

## 6. Measure performance before optimizing

For meaningful performance work follow:

`measure -> locate -> optimize -> measure again`

Prefer avoiding unnecessary work over making unnecessary work slightly faster. Pay particular attention to startup, the WPF UI thread, periodic polling, process enumeration, filesystem/database access, image work and repeated scans.

Do not add caches, workers, timers or concurrency without a demonstrated reason.

## 7. Product quality includes UX

A feature is not complete merely because the code is correct. GameHours should feel coherent, modern, responsive, clear and deliberate.

Reuse the existing visual language and components. Consider hierarchy, spacing, density, focus/keyboard behavior, hover/disabled states, loading, errors, empty states and destructive actions. Do not leave default WPF/Windows styling visible when it conflicts with the GameHours design.

Meaningful visual or interaction changes require real Windows verification when automated tests cannot establish the result.

## 8. Validate before claiming completion

Compilation is not verification. Use tests and validation proportional to the risk of the change, including focused regression tests, the full suite when reasonable, CI, packaging/persistence checks and real-machine verification where appropriate.

Never remove, skip or weaken a valid test merely to obtain a green result. Always distinguish between implemented, compiled, automated-tests-passed, CI-passed and manually/real-machine verified. If something could not be verified, say so explicitly.

## Working interpretation

For substantial product or architectural work, use a lightweight Spec-Driven flow appropriate to the change:

`specification -> clarification -> technical plan -> small tasks -> implementation -> validation`

Do not create documentation for its own sake. Small, obvious fixes do not need heavyweight specs. The code, tests, current specification and observed behavior should remain aligned; when implementation reveals a durable decision that changes the specification or plan, update the relevant document.

Human review remains part of the loop. The agent accelerates engineering; it does not replace engineering judgment.
