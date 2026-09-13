# Portable-save refinements

`portable-save.json` is a small GameHours-owned correction layer used only by the
`portableSave` scope after a game has already been resolved through a stable store
identity. It is deliberately separate from the upstream Ludusavi manifest so broad
upstream entries can be narrowed without adding title checks or filename/extension
heuristics to the engine.

Rules for adding a refinement:

- key it by stable store identity and, when needed, host OS;
- prefer complete semantic save directories over filename extensions;
- replace data only when there is strong external/product evidence that the upstream
  entry is too broad;
- require the upstream paths whose broadness the refinement is correcting, so a
  future upstream manifest change automatically disables a stale refinement and
  falls back to normal conservative manifest handling;
- keep local-only save locations when they are legitimate progress stores, even when
  a launcher cloud declaration does not include them;
- never infer save importance from age, size, filename fragments, or extension;
- keep the default conservative Ludusavi behavior when no refinement matches.

## Valheim / Steam 892970 / Windows

The upstream Ludusavi entry marks the entire
`AppData/LocalLow/IronGate/Valheim` tree as `save`, which also captures logs,
server lists, and large biome-data caches. Steam Auto-Cloud metadata for Valheim
identifies `IronGate/Valheim/characters` and `IronGate/Valheim/worlds` as the
developer-declared cloud roots. Modern Valheim also stores legitimate local-only
progress under the sibling `characters_local` and `worlds_local` directories, so
those are retained as well.

The refinement also lists the Steam-local `remote/characters` and `remote/worlds`
copies explicitly. Ludusavi normally auto-scans the whole Steam `remote` directory
for a known AppID; `suppressImplicitSteamCloudScan` disables that broad implicit
scan only after stable identity resolution, preventing unrelated `serverlist`
payload from being reintroduced.

Evidence used when this refinement was introduced:

- Steamworks/Steam app metadata for AppID 892970 Auto-Cloud roots;
- current Valheim save-layout behavior, including local-only save directories;
- read-only GameHours previews against a real Windows installation showing the
  broad upstream entry pulling cache/log/server-list data while the refined set
  retained character/world progress and Steam-local character/world copies.
