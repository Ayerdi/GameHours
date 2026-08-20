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
- Learned exact executable mappings and manual confirmation for otherwise unknown games.
- Hybrid Windows monitor using process-exit events plus permanent one-second reconciliation.
- Session engine grouping multiple primary processes into one persisted game session.
- Five-second durable open-session checkpoints and conservative interrupted-session recovery.
- Velopack 1.2.0 update-service implementation isolated behind `IAppUpdateService`.
- Reproducible self-contained Windows packaging for beta/stable channels with a pinned `vpk` tool.
- Development `update-check` and `update-now` commands for local or HTTP(S) Velopack feeds.
