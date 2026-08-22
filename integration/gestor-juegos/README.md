# Gestor de Juegos integration

This directory documents the integration boundary only.

GameHours does not import or depend on the Gestor backend. The local vertical slice is now proven with a measured SQLite session, the normalized `GameHours.Sync` contract and a persistent idempotent local transport.

The next integration step belongs in the `gestor-juegos` repository: add the corresponding Flask/PostgreSQL migration and endpoint, keep it compatible with `docs/API-CONTRACT-DRAFT.md`, and use native per-device authentication rather than trusting browser-oriented Authentik headers from the desktop process.
