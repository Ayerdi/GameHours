# GameHours roadmap details

This document expands [`ROADMAP.md`](ROADMAP.md) into implementation guidance. `ROADMAP.md` decides product priority; this file records the problem, intended UX, architecture, delivery sequence, risks and definition of done so future work does not have to reconstruct these decisions from chat history.

The roadmap remains intentionally local-first and avoids turning GameHours into a launcher, storefront or social network.

---

# 1. Cross-cutting product principles

## 1.1 Local truth stays authoritative

Measured GameHours sessions, local achievement evidence, historical-recovery provenance and future save-backup records remain authoritative within their own domains. Optional providers may enrich presentation, but they do not silently replace local truth.

## 1.2 Optional network features must fail soft

Metadata, rarity, external catalogue data and future sync must sit behind provider boundaries with local caches. GameHours must remain useful offline and should never block tracking startup on a remote service.

## 1.3 Keep identities separate from presentation

Do not grow `TrackedGame` into a giant object. Stable tracking identity, user preferences, metadata, health state, save state and presentation read models should remain distinct so failures and migrations stay local to their domains.

## 1.4 Product sophistication should reduce user complexity

A technically sophisticated detector should produce simple user-facing states. Prefer one understandable status plus relevant actions over exposing internal resolver/provider terminology by default.

## 1.5 Preserve uncertainty

GameHours must continue distinguishing measured, reconstructed, estimated, observed and externally sourced information. A richer UI must not make weak evidence look exact.

---

# 2. Engineering rules for every roadmap phase

## 2.1 Research before implementation

Before a significant slice:

- check what GameHours already has;
- review official platform/framework documentation;
- inspect the concrete reference files recorded in [`REFERENCE-PROJECTS.md`](REFERENCE-PROJECTS.md);
- compare the smallest reasonable alternatives;
- document substantial third-party adaptation and licensing in the PR that actually introduces it.

## 2.2 Prefer framework/platform primitives

Use WPF collection views, Windows APIs, SQLite, existing GameHours providers and lifecycle events before adding dependencies or parallel frameworks.

## 2.3 Keep third-party instability behind our contracts

Permissively licensed source may be reused when that is genuinely better than recreating it, but unstable upstream APIs must terminate at a small GameHours-owned boundary.

## 2.4 Small vertical slices

Each roadmap area must be split into independently mergeable PRs with one primary responsibility. A slice should include the persistence, service, UI and tests required to make that one capability complete rather than creating a large speculative framework first.

## 2.5 Measure before optimizing

No new cache, background timer, FTS index, native worker or synchronization mechanism should be added just because it might be useful later. First characterize the workload, then choose the smallest mechanism that solves it.

---

# 3. Library 2.0

## 3.1 User problem

The current recency-first library is appropriate for a small collection and already makes recent/running games easy to reach. It becomes insufficient when the library contains tens or hundreds of entries.

The user needs to answer quickly:

- "where is this game?";
- "what am I currently playing?";
- "which games did I finish?";
- "which ones do I want to come back to?";
- "hide things I do not want in the normal view without deleting tracking history".

## 3.2 Intended UX

Library 2.0 deliberately separates **browsing** from **organization** so the main playtime table does not become a dense settings form.

The normal browsing view remains compact and tracking-oriented:

```text
[ Buscar juegos... ]   [ Mostrar: Todos v ]   [ Organizar biblioteca ]

BIBLIOTECA
  active first -> recently played -> older
  game | last activity | achievements | total | measured | historical
```

`Organizar biblioteca` switches the content of the same Biblioteca section into an explicit management view rather than opening a separate settings window:

```text
Organizar biblioteca                                      [ <- Volver ]
Elige tu estado, marca favoritos y oculta juegos sin tocar su historial.

[ Buscar... ]

JUEGO                 ESTADO        FAVORITO    RESUMEN       VISIBILIDAD
Gothic 1 Remake       [Jugando v]      ★        53,8 h        [Ocultar]
Another Game          [Pendiente v]    ☆        12,1 h        [Ocultar]
```

Important behavior:

- an empty query preserves the current active/recent ordering;
- typing filters immediately without blocking the UI;
- filters combine predictably;
- hidden/archive does not delete the game or its history;
- favorites influence filtering/presentation but do not silently reorder every view unless the chosen sort asks for it;
- user state is editable from the dedicated organizer and may also remain available as a compact right-click shortcut;
- the organizer shows hidden games too, so hiding something can never make it impossible to recover;
- organizer changes persist immediately and returning to browse reuses the same existing view/filter state rather than constructing a second library;
- search behavior should be shared between browse and organizer instead of having two independent matching implementations;
- long-running tracking refreshes should coalesce organizer read-model refreshes instead of rebuilding the full organizer once per collection change.

The dedicated organizer is the primary UX for setting status. Context menus are convenience shortcuts, not discoverability-critical functionality.

## 3.3 Data boundary

Do **not** expand `TrackedGame` into a Playnite-style catch-all object.

Create dedicated user-owned library state, conceptually:

```text
GameLibraryPreferences
- GameId
- IsFavorite
- IsHidden
- CompletionStatus
- UpdatedAtUtc
```

Tags should be normalized separately so they remain queryable and do not become comma-separated strings.

Completion status is a user preference, not inferred truth. GameHours may suggest a status later, but it should not silently mark a game `Completado` simply because achievements reach 100%: some games have no achievements and achievement completion is not equivalent to finishing a game.

For compatibility with the optional Gestor de Juegos adapter, the shared status subset is currently:

- `Pendiente`;
- `Jugando`;
- `Pausado`;
- `Completado`;
- `Abandonado`;
- plus local `Sin estado` when the user has not classified a game.

Do not conflate Gestor `completado_100` or GameHours achievement 100% with the personal completion status.

## 3.4 Search strategy

Start with the simplest implementation that fits the expected library scale:

1. Unicode/case/diacritic normalization;
2. exact/prefix matches first;
3. all query words contained in the title next;
4. acronym matching where useful;
5. deterministic recency/title tie-breaking.

Do not introduce Lucene, Elasticsearch, SQLite FTS or a fuzzy-search package in the first slice. If profiling with genuinely large libraries shows that in-memory/simple SQL filtering is insufficient, then measure and revisit.

Playnite's search implementation is a useful reference for asynchronous/cancellable UX, but its plugin/search-context machinery is larger than GameHours needs.

## 3.5 Optional metadata enrichment

Metadata is a later sub-phase. The desired boundary is conceptually:

```text
IGameMetadataProvider
        |
        v
GameMetadataResult
        |
        v
local metadata cache + provenance
        |
        v
Library/Game Detail presentation
```

Candidate fields:

- cover/background;
- developer/publisher;
- release date;
- genres;
- short description;
- platform/store identity where useful.

Requirements:

- cancellation;
- partial results;
- provider provenance;
- cached artwork/metadata;
- clear refresh behavior;
- offline fallback;
- provider failure never blocks the library.

Do not let metadata providers write executable mappings or measured-history identity.

## 3.6 Delivery slices

**Library 2.0A — preferences + search + explicit organizer**

- favorite;
- hidden/archive;
- completion status;
- search;
- quick filters;
- dedicated `Organizar biblioteca` UX;
- right-click actions retained as shortcuts;
- persistence/migrations/tests.

**Library 2.0B — tags + filter polish**

- user tags;
- combined filters;
- empty states;
- keyboard/focus behavior;
- performance validation with a synthetic large library.

**Library 2.0C — metadata provider boundary**

- provider contract;
- one concrete provider only;
- cache/provenance;
- graceful offline/error states.

**Library 2.0D — visual polish**

- richer cards/detail hierarchy;
- artwork transitions/loading states;
- density settings only if real use shows a need.

## 3.7 Definition of done

Library 2.0 is successful when a user with a large local history can find and organize a game quickly without changing or endangering the tracking identity underneath it. In particular, setting a status/favorite/visibility must be discoverable without knowing that a context menu exists.

---

# 4. Game Health and supportability

## 4.1 User problem

GameHours already knows a great deal about why a game is or is not being tracked: executable resolution, learned mappings, source health, achievement state, historical evidence and reconciliation. Much of that knowledge currently lives in diagnostics/logs or requires understanding implementation details.

The product should answer a simpler question:

> Is this game working correctly in GameHours, and if not, what should I do?

## 4.2 Intended UX

Per-game health should collapse technical detail into one status:

```text
Correcto
Necesita atención
No se está siguiendo
```

Example healthy state:

```text
Estado de Gothic 1 Remake: Correcto
Tracking        correcto
Ejecutable      reconocido
Historial       disponible
Logros          fuente detectada
Última medida   hace 2 min

[ Ver detalles técnicos ]
```

Problem state:

```text
Necesita atención
GameHours ha observado el proceso pero no puede asociarlo con suficiente confianza.

[ Resolver ]
```

## 4.3 Architecture

Do not create another scanner. Build a projection over existing authoritative services/repositories, conceptually:

```text
GameHealthSnapshot
- OverallStatus
- IdentityStatus
- TrackingStatus
- LastObservation
- HistoricalRecoveryStatus
- AchievementStatus
- NotificationStatus
- SaveSafetyStatus (later)
- Issues[]
```

Each issue should have:

- stable code;
- user-facing explanation;
- severity;
- optional existing action identifier;
- optional technical detail.

The read model must not mutate anything.

## 4.4 Guided actions

The first slice is read-only. After the health model proves useful, guided repair actions may invoke existing workflows such as candidate confirmation, executable-role override, source refresh or future save mapping.

Do not put destructive "fix automatically" logic into diagnostic checks.

## 4.5 Diagnostic bundle

Before beta, add one-click export that can include:

- GameHours version/build;
- Windows/.NET summary;
- sanitized preferences;
- relevant diagnostics/logs;
- provider/source summaries;
- schema/application ID;
- optionally game-specific health details.

Centralize redaction. Paths containing Windows user names, tokens, secrets or unnecessarily identifying information should be removed or normalized before the archive is created.

Playnite's MIT `Diagnostic.CreateDiagPackage` is a useful implementation reference, but GameHours should apply stricter privacy defaults.

## 4.6 Definition of done

A non-technical user should be able to tell whether a game is tracked correctly and obtain actionable information without reading raw logs.

---

# 5. Achievements 2.0

## 5.1 User problem

The achievement engine already distinguishes complete catalogues, incomplete state, historical uncertainty and supplemental positive evidence. The UI should make that sophistication useful rather than exposing only compact counts.

## 5.2 Near-term UX

- modern Windows toast when a genuinely new unlock survives existing baseline/session gates;
- locked/unlocked/hidden/progress filters;
- progress presentation only when the source supplies real progress;
- stronger 100% completion moment and game-detail hierarchy;
- keep "hora histórica no disponible" or equivalent when timestamps are not trustworthy.

Notification transport must remain behind the existing neutral achievement event so detection/persistence do not depend on Windows toast APIs.

## 5.3 Rarity

Rarity is optional enrichment, not local truth.

Desired cached model:

```text
AchievementRarity
- GameId
- AchievementApiName
- UnlockPercent
- Tier
- Provider
- ObservedAtUtc
```

If the network/provider is unavailable, achievements still function normally.

Do not infer unlock state from rarity data.

## 5.4 Screenshot souvenir

Treat this as an experiment only after notifications are solid. If implemented:

- opt-in;
- no anti-cheat-sensitive hooking;
- capture using supported Windows mechanisms;
- bounded storage/retention;
- clear indication if capture failed;
- unlock detection must not wait for the screenshot.

## 5.5 Definition of done

Achievements feel integrated and polished while evidence uncertainty remains accurate and local operation remains independent of online rarity/metadata.

---

# 6. Save Safety — integrated Ludusavi engine

## 6.1 User problem

Save protection is highly valuable but game-save discovery is a large solved problem with thousands of game-specific layouts, registry entries, path variables and store IDs. Recreating that manifest and scanner in GameHours would add enormous maintenance cost for little differentiation.

At the same time, requiring users to separately install/configure another application weakens GameHours' product experience.

## 6.2 Upstream facts and constraints

Ludusavi is MIT licensed. Its current Rust package separates the user-facing application behind the Cargo `app` feature and exposes library modules such as scanning/path/resource/API/serialization. Its `lib.rs` also warns that the library API is unstable and many internals were not originally designed as a stable public API.

Therefore:

- source-level reuse is legally and technically possible;
- direct coupling throughout .NET would be a maintenance mistake;
- copying/porting the engine into C# would create a fork we must maintain;
- the correct value to reuse is the engine/manifests, not Ludusavi's GUI.

## 6.3 Preferred architecture

```text
GameHours.Desktop / .NET
          |
          | stable, versioned GameHours JSON protocol
          v
GameHours.SaveEngine
small Rust executable shipped in the GameHours package
          |
          | exact pinned Ludusavi revision
          v
Ludusavi core + ludusavi-manifest
```

`GameHours.SaveEngine` is part of GameHours from the user's perspective. It is not a separately installed application and does not have its own tray icon/settings UX.

Prefer a one-shot helper process at first rather than a resident daemon:

- no permanent extra process;
- clean cancellation/timeout boundary;
- process crash cannot corrupt the desktop host;
- JSON protocol is easier to version/test than exposing unstable Rust structs over FFI/ABI.

Potential GameHours protocol operations:

```text
GetCapabilities
ResolveGameSaveData
PreviewBackup
CreateBackup
ListBackups
PreviewRestore
RestoreBackup
```

Each response should carry a protocol version and structured result/error codes rather than human CLI strings.

Candidate errors:

```text
UnsupportedGame
AmbiguousMapping
NoSaveData
AccessDenied
Busy
BackupFailed
InvalidBackup
RestoreConflict
ProtocolMismatch
EngineFailure
```

## 6.4 Responsibility split

Ludusavi-derived engine is responsible for:

- manifest interpretation;
- save-file/registry path expansion;
- scanning;
- backup mechanics;
- restore mechanics;
- backup format where reused.

GameHours is responsible for:

- mapping GameHours identity to the appropriate upstream identity;
- lifecycle/when operations happen;
- user consent/settings;
- background scheduling and cancellation;
- presenting support/health/history;
- automatic backup policy;
- backup retention UX/policy;
- restore confirmation/conflict policy;
- making sure Save Safety failures never damage tracking/achievement state.

## 6.5 Identity mapping

Use the general GameHours external-identity boundary rather than title-only matching where possible:

```text
GameHours UUID
  -> Steam AppID / GOG ID / Epic identity / etc.
  -> Ludusavi manifest identity
```

Title matching may be a fallback that requires confirmation when ambiguous.

Store a verified mapping so every backup does not repeat expensive or ambiguous discovery.

## 6.6 Automatic backup lifecycle

GameHours already knows when a measured session completes. Reuse it:

```text
SessionCompleted
      |
      v
SaveBackupCoordinator
      |
      +-- auto backup disabled -> stop
      |
      +-- unsupported/ambiguous -> record health state, do not block session finalization
      |
      v
GameHours.SaveEngine CreateBackup
```

Do **not** add another process watcher just for saves.

Automatic work should happen after authoritative session persistence. A backup failure can be surfaced/retried but cannot roll back or invalidate the measured session.

If multiple rapid session boundaries occur for one game, coalesce/serialize appropriately rather than creating simultaneous backups of the same save set.

## 6.7 Restore safety

Restore is intentionally later than backup. It can destroy newer save data and therefore requires stronger UX and validation.

Before any destructive restore:

1. preview affected files/registry values;
2. verify backup integrity/version;
3. create a safety backup of current state where possible;
4. present explicit confirmation;
5. handle game-running/busy state;
6. execute restore;
7. verify result;
8. retain enough audit data to understand what happened.

Avoid a one-click destructive restore in early slices.

## 6.8 Delivery slices

### Save Safety 1 — bridge feasibility

Goal: prove the hardest boundary before building UX.

- add smallest Rust helper project;
- pin an exact Ludusavi revision;
- compile only required core/library surface where feasible;
- define versioned JSON envelope;
- implement `GetCapabilities` plus one read-only save-data/preview operation;
- .NET process wrapper with cancellation, timeout, stdout size bound and structured errors;
- Rust + .NET contract tests;
- CI build for win-x64;
- include helper in publish/package smoke;
- establish `THIRD-PARTY-NOTICES.md`, upstream revision record and license verification.

Do not enable automatic backup yet.

### Save Safety 2 — manual backup

- map a real GameHours game to manifest identity;
- show detected save locations/count/size in a preview;
- `Crear copia ahora`;
- clear unsupported/ambiguous/permission failure UX;
- list latest successful backup in game detail/health.

### Save Safety 3 — automatic after session

- opt-in globally and/or per game;
- trigger from existing `SessionCompleted`;
- background execution;
- coalesce per game;
- don't block session persistence/application shutdown indefinitely;
- visible result/health state.

### Save Safety 4 — history and retention

- backup history;
- storage usage;
- retention policy;
- delete/cleanup UX with safe defaults.

### Save Safety 5 — restore

Only after the backup path is mature:

- preview restore;
- current-state safety backup;
- confirmation;
- running-game protection;
- restore verification;
- conflict/version handling.

## 6.9 Upstream update strategy

Do not track Ludusavi `master` implicitly.

Maintain a record containing:

- repository;
- exact commit/tag;
- Ludusavi version if applicable;
- manifest revision;
- local integration patch list if any;
- date reviewed;
- licenses.

Upstream update PRs should run the SaveEngine contract suite and representative manifest fixtures before changing the pinned revision.

## 6.10 Licensing and attribution

The first PR that actually distributes Ludusavi source/binary-derived content or `ludusavi-manifest` must add the required MIT notices.

Expected repository-level artifact:

```text
THIRD-PARTY-NOTICES.md
- Ludusavi
  copyright
  MIT text / pointer according to packaging arrangement
  upstream URL
  exact revision
- ludusavi-manifest
  corresponding notice/revision
```

Package verification must ensure the notices ship with the product where required.

Do not create a misleading notice before any third-party code/data is actually distributed.

## 6.11 Alternatives rejected

### Require separately installed Ludusavi

Simple technically, but worse product UX and creates version/path/configuration dependency on another application. Keep it only as a possible developer/debug fallback, not the intended product path.

### Port the engine to C#

Rejected: high ongoing maintenance and loss of upstream improvements.

### Vendor the whole Ludusavi application

Rejected: unnecessary GUI/CLI/cloud/translations/dependencies and larger attack/maintenance surface.

### Broad direct FFI

Rejected initially: unstable Rust types/ABI would leak throughout GameHours and make upstream updates expensive. A small process/JSON boundary is easier to reason about and recover from.

## 6.12 Definition of done

Save Safety should feel native to GameHours: the user should not have to know Ludusavi exists to protect saves. Internally, upstream reuse must remain obvious, pinned, licensed and isolated enough that a Ludusavi update does not require rewriting GameHours UI/domain code.

---

# 7. Platform expansion

## 7.1 Distinguish capability layers

Never describe a platform as simply "supported" without saying what works.

For every platform track separately:

```text
Discovery
Tracking
Achievements
Metadata
Historical recovery (if applicable)
```

Example:

```text
Xbox / Microsoft Store
Discovery: yes
Tracking: yes
Achievements: not yet
Metadata: partial
```

This avoids coupling platform discovery work to a much harder achievement integration.

## 7.2 Priority order

Current candidate order:

1. Xbox / Microsoft Store / PC Game Pass;
2. Ubisoft Connect;
3. EA Desktop;
4. Amazon Games / Battle.net if stable local evidence is available;
5. emulators based on real user cases, not a speculative universal emulator framework.

For each platform first investigate official/local manifests/package APIs and what Playnite/Heroic/Achievement Watcher currently use. Prefer documented/local stable identity over executable-name heuristics.

## 7.3 Performance rule

Platform discovery should remain event/startup/manual-refresh oriented where possible. Do not add high-frequency filesystem/registry scans to the tracking loop.

## 7.4 Definition of done

Each shipped platform states exactly which capabilities work and does not reduce existing tracking precision/reliability for other games.

---

# 8. Insights 2.0

## 8.1 Opportunity

GameHours has a differentiator that traditional launcher playtime often lacks: it can distinguish executed time, foreground time and active-estimated time for measured sessions while preserving historical evidence separately.

Use that data to answer useful personal-history questions rather than creating dashboards for their own sake.

Candidate insights:

- average/median session length;
- longest session;
- playtime by day of week/hour;
- monthly/weekly trends;
- heatmap/calendar summaries;
- executed vs focused vs active-estimated;
- focus ratio;
- streaks;
- achievement activity;
- per-game drill-down.

## 8.2 Coverage-aware ratios

Do not compute misleading lifetime ratios when attention telemetry only exists for recent measured sessions.

For example:

```text
focus_ratio = focused_time / executed_time
```

must use only intervals/sessions where both signals have valid coverage.

Presentation should state the coverage denominator, for example:

> Foco 83 % · basado en 24 sesiones con telemetría

Do not blend reconstructed historical playtime into active/focus denominators unless the source genuinely provides equivalent information.

## 8.3 Performance

Prefer SQLite aggregation for genuinely large historical queries rather than materializing all sessions repeatedly in WPF.

Before adding indexes, measure representative query plans/timing and add only those justified by actual queries.

## 8.4 Definition of done

Insights reveal patterns a user could not easily get from Steam/launcher totals while remaining statistically honest about coverage.

---

# 9. First-run, help and beta UX

## 9.1 First-run goal

A new user should understand what GameHours does without reading the repository.

Possible first-run flow:

```text
Bienvenido a GameHours
Tu historial local de juego, independiente del launcher.

Detectando fuentes...
✓ Steam
✓ Epic
✓ GOG

GameHours funciona en segundo plano y empieza a medir desde ahora.
[ Empezar ]
```

Do not force optional network providers/accounts during onboarding.

## 9.2 Contextual help

Prefer explanations based on actual state:

- why a game is missing;
- what "histórico estimado" means;
- why achievement time is unavailable;
- why active time differs from executed time;
- why a game needs attention.

Link directly to the relevant action/section where possible.

## 9.3 Empty/loading/error states

Every major view/provider should define:

- initial loading;
- empty but healthy;
- offline/unavailable optional provider;
- recoverable error;
- permanent unsupported state.

Avoid presenting blank tables that look broken.

## 9.4 Accessibility

Before beta review:

- keyboard navigation/focus order;
- focus visuals;
- high-DPI scaling;
- color contrast;
- screen-reader labels for icon-only controls;
- reduced-motion consideration if animations are added;
- avoid using color as the only status signal.

## 9.5 Localization foundation

Do not hardwire future provider/error logic to Spanish display text. Stabilize user-facing message identifiers/models first, then move strings toward resources when the beta UX is sufficiently settled.

## 9.6 Definition of done

A first-time user can install, understand, diagnose common issues and find their data/privacy controls without external instructions.

---

# 10. Distribution and trust

## 10.1 Signing

Move from internal unsigned/dev validation to a repeatable Azure Artifact Signing release path.

Requirements:

- OIDC identity;
- minimum required signer role;
- no long-lived signing secret in GitHub;
- sign every executable/DLL that requires Authenticode trust, including future native helpers such as `GameHours.SaveEngine.exe`;
- verify signatures after packaging.

## 10.2 Release gates

For a beta candidate verify:

- clean install;
- launch;
- single instance;
- update from previous signed version;
- recovery behavior;
- uninstall preserving external data by design;
- reinstall finds preserved data;
- package hashes/signatures;
- updater source remains HTTPS/read-only/trusted according to existing policy.

## 10.3 SmartScreen

Evaluate reputation with the actual signed release binary rather than extrapolating from unsigned development builds.

## 10.4 Public documentation

At minimum:

- what GameHours measures;
- where local data lives;
- backup/restore/export/import;
- install/update/uninstall;
- privacy/network behavior;
- limitations of historical recovery;
- troubleshooting/diagnostic export.

## 10.5 Definition of done

A beta can be installed and updated by another Windows user with understandable trust/privacy behavior and a recovery path if something goes wrong.

---

# 11. Later architectural opportunities

## 11.1 Public plugin/provider SDK

Do not freeze one yet. Exercise internal provider boundaries through multiple real platform/metadata/save integrations first. Once the contracts stop changing frequently, evaluate exposing a deliberately small public SDK.

## 11.2 Local API

ActivityWatch demonstrates the flexibility of an API/event model. GameHours should only add a local API if concrete automation/integration use cases justify the security/lifecycle surface.

## 11.3 Cloud/sync

Optional only. Never make account/cloud availability a prerequisite for local tracking or local history access.

---

# 12. Roadmap-wide definition of quality

A roadmap item is not complete simply because it compiles.

Depending on the slice, completion requires:

- root cause/problem clearly characterized;
- architecture consistent with existing boundaries;
- no unnecessary dependency/parallel mechanism;
- migrations and failure states considered;
- focused automated tests;
- full applicable suite;
- Release build;
- CI/CodeQL where applicable;
- real-Windows functional/visual verification where CI cannot prove behavior;
- updated docs/attribution when required;
- no claim stronger than the evidence actually collected.

The goal is not to maximize feature count. The goal is for every addition to make GameHours more useful without degrading its reliability, clarity or maintainability.
