# Gestor de Juegos integration

This directory is the optional adapter boundary between GameHours and `Ayerdi/gestor-juegos`. GameHours remains fully local-first and must continue working without an account, network connection or Gestor deployment.

The canonical GameHours sync model lives in [`../../docs/SYNC-BOUNDARY.md`](../../docs/SYNC-BOUNDARY.md) and uses GameHours-owned UUIDs. Gestor catalogue IDs, authentication and endpoint behaviour must not leak into `GameHours.Core` or the neutral sync contracts.

## Compatibility foundation

Library 2.0 keeps the two products compatible without coupling them:

- GameHours keeps its UUID as the authoritative tracking identity;
- provider-scoped identities such as `steam:3946950` can be persisted in `game_external_identities` and are the preferred correlation key for optional catalogue providers;
- a future Gestor adapter may resolve `steam:<appid>` against `catalogo_juegos.steam_id` and cache the resulting `catalogo_juego_id`, but that Gestor-local ID never replaces the GameHours UUID;
- `favorito` maps naturally to GameHours `IsFavorite`;
- the shared personal states are `Pendiente`, `Jugando`, `Pausado`, `Completado` and `Abandonado`;
- GameHours `IsHidden` is local presentation state and has no Gestor equivalent, so an external provider must not overwrite it;
- GameHours measured playtime remains authoritative local evidence. Gestor `tiempo_jugado` and Steam snapshots are optional external information and must not rewrite measured sessions.

The reviewed Gestor field/API mapping and conflict rules live in [`API-CONTRACT-DRAFT.md`](API-CONTRACT-DRAFT.md). The actual network adapter remains deferred: this foundation deliberately adds no remote request, credential or startup dependency.
