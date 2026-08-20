# Gestor de Juegos sync contract — draft

This is deliberately a draft until the local engine produces real sessions.

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

Backend integration resumes after the first local session can travel through Core -> SQLite reliably.
