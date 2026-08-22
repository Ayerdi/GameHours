# Gestor de Juegos adapter contract — deferred draft

This document belongs to the optional Gestor de Juegos integration, not to the backend-neutral GameHours sync contract.

GameHours emits its own UUID-based model described in `../../docs/SYNC-BOUNDARY.md`. A future Gestor adapter will be responsible for resolving a GameHours `game_id` to a Gestor `catalogo_juego_id`, authenticating the native device and translating the neutral model into the Gestor API shape.

Possible Gestor-side payload shape:

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
  ],
  "historical": []
}
```

This shape is intentionally deferred and may change when the Gestor integration is resumed. It must not leak back into `GameHours.Core` or the neutral `GameHours.Sync` contracts.

The native client must use a dedicated device/account credential flow rather than spoofable browser-oriented Authentik headers.
