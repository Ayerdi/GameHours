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
- Hybrid Windows monitor using process-exit events plus permanent one-second reconciliation.
- Session engine grouping multiple primary processes into one persisted game session.
