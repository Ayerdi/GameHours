# Backend-neutral sync boundary

GameHours is local-first. Tracking, historical reconstruction, achievements and persistence must work without any account, network connection or external backend.

`GameHours.Sync` therefore exposes only GameHours-owned identities and normalized playtime data. It does not know about Gestor de Juegos catalogue IDs, users, authentication headers or endpoint paths.

## Neutral measured-session shape

The local/test transport serializes the contract with snake_case JSON names:

```json
{
  "tracking_started_at_utc": "2026-08-22T16:00:00Z",
  "sessions": [
    {
      "client_session_id": "...",
      "game_id": "...",
      "started_at_utc": "2026-08-22T16:10:00Z",
      "ended_at_utc": "2026-08-22T16:42:00Z",
      "capture_method": "reconciliation",
      "confidence": "high"
    }
  ],
  "historical": []
}
```

`game_id` is the GameHours UUID. External integrations are responsible for translating it to their own identity model.

## Rules

- measured sessions before `tracking_started_at_utc` are rejected before transport;
- estimated confidence is not valid for measured sessions;
- client-generated session/evidence UUIDs are the idempotency keys;
- retrying identical data with the same UUID is a duplicate, not additional playtime;
- retrying the same UUID with different data is an idempotency conflict;
- titles, executable paths, PIDs, Windows usernames, raw SRUM and registry data are not part of the boundary;
- external catalogue mappings and authentication belong to adapters, not `GameHours.Core` or the neutral sync contract.

## Integration adapters

An adapter may transform the neutral shape for a specific backend. For example, a future Gestor de Juegos adapter may map:

```text
GameHours game_id UUID -> Gestor catalogo_juego_id
```

and rename fields to the backend API contract. That translation must remain outside the GameHours tracking core and neutral sync model.

The persistent local transport exists to validate serialization, idempotency and retry behaviour without requiring any backend.
