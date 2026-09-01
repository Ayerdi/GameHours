# Gestor de Juegos adapter contract — compatibility draft

This document belongs to the optional Gestor de Juegos integration, not to the backend-neutral GameHours sync contract. It was reviewed against the current `Ayerdi/gestor-juegos` schema/API documentation and `main` implementation on 2026-08-31.

GameHours remains the tracking authority and emits its UUID-based model described in `../../docs/SYNC-BOUNDARY.md`. A future adapter may enrich or synchronize selected library fields, but the adapter must be removable without changing local tracking behaviour.

## Identity mapping

Never use a Gestor database primary key as the canonical GameHours identity.

Preferred matching order:

1. `steam:<appid>` from GameHours `game_external_identities` -> Gestor `catalogo_juegos.steam_id`;
2. `igdb:<id>` -> Gestor `catalogo_juegos.igdb_id` when GameHours has a verified IGDB identity in the future;
3. title matching only as an explicit user-assisted fallback, never as silent authoritative identity.

After a verified match, an adapter may cache `catalogo_juego_id` as a Gestor-specific link. That cached link is replaceable integration state; the GameHours UUID and measured history remain valid if the Gestor is unavailable or rebuilt.

Provider IDs are namespaced. `steam:123` and `gog:123` are different identities. Provider names are normalized by GameHours, while external identity values remain exact so each provider adapter owns any provider-specific normalization. One provider identity must not silently move between two GameHours games.

## Library state mapping

The current common personal-state subset is:

| GameHours | Gestor `mis_juegos.estado` |
| --- | --- |
| `Backlog` | `Pendiente` |
| `Playing` | `Jugando` |
| `Paused` | `Pausado` |
| `Completed` | `Completado` |
| `Abandoned` | `Abandonado` |
| `Unspecified` | no imported state |

Gestor also supports states such as `Deseado`, `En Espera` and `Wishlist`. GameHours must not coerce those into a different completion state. Until GameHours intentionally adds an equivalent concept, an adapter should preserve them as source-specific information or leave local completion status unchanged.

`mis_juegos.favorito` maps to GameHours `IsFavorite`.

Gestor `completado_100` is a separate flag and must **not** be translated into `LibraryCompletionStatus.Completed`; GameHours completion status maps only from `mis_juegos.estado`. Achievement completion and “finished the game” are deliberately different concepts in GameHours as well.

GameHours `IsHidden` is local-only presentation state. There is no equivalent field in the reviewed Gestor schema, so remote data must never clear or set it.

## Field authority

| Information | Authority / rule |
| --- | --- |
| GameHours UUID | GameHours only |
| measured sessions and focused/active telemetry | GameHours only; never overwritten by Gestor |
| reconstructed SRUM history | GameHours evidence; never converted into exact Gestor/Steam truth |
| `favorito` / completion status | optional library sync; conflict policy must be explicit before bidirectional writes are enabled |
| `tiempo_jugado` | external/personal Gestor information; may be displayed or imported as separately labelled evidence, not written over measured sessions |
| `horas_steam_snapshot`, Steam achievement snapshots | external snapshots only |
| cover, developer, publisher, release date, genres and similar catalogue metadata | optional enrichment with provider provenance/cache |
| hidden/archive | GameHours local only |

A first integration should therefore be read-only enrichment/import. Bidirectional preference writes should be a later opt-in feature with a visible conflict policy rather than last-write-wins by accident.

## Current Gestor surfaces relevant to a future adapter

The reviewed Gestor exposes a global `catalogo_juegos` model and a separate `mis_juegos` user relationship. This separation matches GameHours' decision to keep external catalogue identity separate from local user preferences.

Useful current endpoints include:

- `GET /mis-juegos` for the authenticated user's personal library;
- `GET /mis-juegos/detalle/<id>?fast=1` for DB-only detail;
- `GET /mis-juegos/detalle/<id>/enrich` for slower external enrichment;
- `GET /catalogo/buscar-o-importar?nombre=...` for catalogue lookup/import.

The exact response shape remains owned by the Gestor repository and must be translated inside the adapter rather than copied into `GameHours.Core`.

## Authentication

The desktop client must use a dedicated native device/account credential flow. It must not spoof browser-oriented Authentik identity headers. No credential or Gestor URL is required for GameHours startup or tracking.

## Deferred playtime upload shape

If session upload is resumed later, the adapter may translate a neutral GameHours session after resolving the local UUID to a Gestor catalogue entry, for example:

```json
{
  "tracking_started_at": "2026-08-22T16:00:00Z",
  "sessions": [
    {
      "client_session_id": "...",
      "catalogo_juego_id": 381,
      "started_at": "2026-08-22T16:10:00Z",
      "ended_at": "2026-08-22T16:42:00Z",
      "capture_method": "reconciliation",
      "confidence": "high"
    }
  ]
}
```

This wire shape is still deferred and may change. It must not leak into the backend-neutral GameHours sync contracts.
