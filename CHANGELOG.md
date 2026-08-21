# Changelog

All notable changes will be documented here.

## [Unreleased]

### Added
- Initial .NET 8 solution architecture.
- Core play-session and historical-evidence domain model.
- Local SQLite persistence and tracker cutover state.
- Timeline rules preventing baseline/gap overlap with measured sessions.
- Initial Windows process snapshot provider and sync contracts.
- Unit/integration tests for core timeline and SQLite repositories.
- Windows GitHub Actions CI for restore, Release build and full solution tests on the desktop-foundation branch and pull requests.
- Layered installed-game discovery for Steam, Epic and GOG.
- Conservative launcher-independent Unreal/Unity runtime discovery.
- Learned exact executable mappings and manual confirmation for unknown executables.
- Hybrid Windows monitor using process-exit events plus permanent one-second reconciliation.
- Session engine grouping multiple primary processes into one persisted game session.
- Five-second durable open-session checkpoints and conservative interrupted-session recovery.
- Intentional tracker cancellation now finalizes active measured segments at the exact graceful shutdown boundary while unexpected monitor termination preserves checkpoint recovery semantics.
- Host-neutral graceful-shutdown signaling for the tray/update coordinator; console control handling remains a development fallback rather than a production lifecycle dependency.
- Windows sleep/resume detection using biased versus unbiased system uptime so suspended time is not counted as playtime.
- Initial `GameHours.Desktop` WPF shell with notification-area lifecycle, live tracker/game status, local measured-plus-estimated playtime library, graceful Exit and per-user Windows autostart.
- Desktop navigation for Library, Activity and Settings, including last-activity metadata and recent measured-session history.
- Unified desktop activity timeline that combines measured sessions with persisted achievement unlocks while preserving whether an achievement timestamp is source-exact or only the moment GameHours observed it.
- Local executable-icon enrichment for remembered games, dark desktop scrollbars and second-level formatting for short activity sessions.
- In-window game detail view with local icon, first known activity, first measured session, measured-session count, measured/historical breakdown, remembered executable and game-scoped activity timeline.
- Read-only local achievement-source probe for game files, Steam caches, Steam-compatible local saves and likely per-game save directories.
- Read-only GSE/Goldberg achievement reader with local definitions, user unlock state, unlock timestamps, progress and artwork rendered in the game detail view.
- Provider-chain abstraction and Windows-specific automated tests for local achievement catalog parsing and provider selection.
- Local-only achievement source locator covering Steam library cache plus common Steam-compatible emulator/save layouts without calling Hydra Cloud or any remote achievement service.
- Steam `librarycache` local-state parsing plus common local state parsers for CODEX, RUNE, OnlineFix, EMPRESS, RLD, SKIDROW, CreamAPI, RLE, Razor1911, `user_stats.ini`, 3DM and ALI213-compatible files.
- Bounded read-only Steam Binary KeyValues parsing for official `UserGameStatsSchema_<appid>.bin` catalogues and per-account `UserGameStats_<account>_<appid>.bin` unlock state, with ambiguous Steam accounts deliberately left unmerged.
- Partial achievement-state presentation that never treats an incomplete local source as the full catalogue.
- Achievement aggregation that combines a complete local catalogue with unlock state from multiple compatible local sources while preserving catalogue totals and earliest known unlock timestamps, while keeping official Steam and emulator installations isolated.
- Durable SQLite achievement state with monotonic unlock semantics, rich-metadata preservation, first/last observation timestamps and first-unlocked observation tracking.
- Baseline-aware achievement observation so historical unlocks discovered on first scan are stored without becoming notification candidates, while later locked-to-unlocked transitions are surfaced for future notifications.
- Read-only SQLite achievement activity queries for per-game completion summaries and recent unlock history, preserving whether an activity time is source-exact or a GameHours observation fallback.
- Session-scoped background achievement monitoring tied to measured `SessionStarted`/`SessionCompleted` events, using cheap state-file fingerprint polling and low-frequency source rediscovery.
- Exit-flush reconciliation with an immediate post-session read plus one bounded delayed retry for formats that finish writing achievement state just after process exit.
- Session notification gating that suppresses first-ever and immediate session baselines, supports late first reads from exit-flush formats with an existing durable baseline, deduplicates API names and rejects clearly stale unlock timestamps before emitting a transport-neutral `AchievementUnlocked` event.
- Notification-area balloon fallback for live achievement unlocks, keeping presentation separate from detection so a native Windows toast transport can be added later.
- Debounced read-only achievement file watching in the game detail view so the visible local list refreshes automatically without owning persistence or consuming background notification transitions.
- Velopack 1.2.0 update-service implementation isolated behind `IAppUpdateService`.
- Reproducible self-contained Windows packaging for beta/stable channels with a pinned `vpk` tool.
- Development `update-check` and `update-now` commands for local or HTTP(S) Velopack feeds.
- ManagedEsent-based read-only `srum-inspect` diagnostic for discovering the real Windows SRUM schema before implementing historical imports.
- Read-only `srum-preview` and conservative `srum-normalize` flows with current-user filtering, NT-device-path resolution, helper exclusion and canonical game matching.
- Guarded, explicitly filtered SRUM baseline import producing deterministic/idempotent `HistoricalEvidence` instead of fake historical sessions.

### Validated
- Packaged beta install and `0.1.0 -> 0.1.1` self-update on a real Windows host, including a generated delta package, graceful updater handoff, restart, version transition and persistence of the existing GameHours SQLite database.
- Live Windows SRUM Application Resource Usage schema, `FaceTime` units, current-user filtering and conservative normalization against real Gothic 1 Remake and Project P.I.T.T. data.
- Explicit in-process graceful shutdown with an active game, including SQLite session finalization, checkpoint removal and clean tracker restart without checkpoint recovery.
- Real Windows suspend/resume with Project P.I.T.T. left running: the pre-sleep segment stopped before suspension and a new segment started after resume, leaving the suspended interval uncounted.
- Project P.I.T.T. local achievement parsing against real GSE files: 23 definitions, 4 unlocked achievements and unlock timestamps resolved without Steam Web API or Internet access.
