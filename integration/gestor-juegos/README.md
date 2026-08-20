# Gestor de Juegos integration

This directory documents the integration boundary only.

GameHours does not import or depend on the Gestor backend. Once the local vertical slice is proven, the corresponding Flask/PostgreSQL migration and endpoints should be implemented in the `gestor-juegos` repository and kept compatible with `docs/API-CONTRACT-DRAFT.md`.
