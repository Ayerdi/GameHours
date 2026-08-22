# Gestor de Juegos sync contract — draft

This remains a draft until the real Gestor backend endpoint and native-client authentication are implemented and validated.

The local vertical slice is now proven: a measured session persisted in GameHours SQLite is translated through `GameHours.Sync` into this normalized payload, accepted by a persistent local transport and safely reported as a duplicate when the same client UUID is retried from a new client instance. Sessions without a Gestor catalog mapping are not sent, and measured sessions before `tracking_started_at` are rejected before transport.

The future native client API will use per-device credentials and idempotent client UUIDs. It must not trust spoofable Authentik headers from the native process.

Expected normalized payload shape:

```json
{
  "tracking_started_at": "2026-08-20T18:00:00Z",
  "sessions": [
    {
      "client_session_id": "...",
      "catalogo_juego_id": 381,
      "started_at": "2026-08-20T18:10:21.953603Z",
      "ended_at": "2026-08-20T18:11:27.133611Z",
      "capture_method": "reconciliation",
      "confidence": "high"
    }
  ],
  "historical": []
}
```

The local transport intentionally persists only normalized sync data and idempotency state. Game titles, local game UUIDs, executable paths, PIDs, Windows usernames, raw SRUM and registry data are not part of this payload.

Next backend step: implement the corresponding idempotent Flask/PostgreSQL endpoint in `gestor-juegos`, then replace the local transport with an authenticated HTTP client while preserving this boundary and its tests.
