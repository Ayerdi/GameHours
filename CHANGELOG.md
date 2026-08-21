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
