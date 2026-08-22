# Gestor de Juegos integration

This directory is an optional adapter boundary. GameHours itself remains backend-neutral and does not import or depend on the Gestor backend.

The canonical GameHours sync model lives in [`../../docs/SYNC-BOUNDARY.md`](../../docs/SYNC-BOUNDARY.md) and uses GameHours-owned UUIDs. Any Gestor-specific catalogue mapping, field translation, authentication or endpoint behaviour belongs here or in the `gestor-juegos` repository, not in `GameHours.Core` or the neutral sync contracts.

The deferred Gestor wire draft is documented in [`API-CONTRACT-DRAFT.md`](API-CONTRACT-DRAFT.md). Integration work is intentionally paused while GameHours continues maturing as a standalone application.
