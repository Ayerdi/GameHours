# GameHours roadmap — detailed product design

**Status:** active companion to [`ROADMAP.md`](ROADMAP.md).

`ROADMAP.md` defines priority and direction. This document explains the intended product outcome, architectural boundaries, staged delivery, risks and definition of done for each roadmap area.

This is a design guide, not a promise that every implementation detail is frozen. Before each implementation slice, re-check the current GameHours code, official platform documentation and the external references in [`REFERENCE-PROJECTS.md`](REFERENCE-PROJECTS.md). Evidence from real Windows installations can change a proposed implementation.

---

# 1. Product thesis

GameHours should become the **reliable personal history of the user's videogames**.

The product should answer, clearly and with provenance:

- what did I play?;
- when did I play it?;
- how long was it really running?;
- how much of that time was focused/active when that coverage is available?;
- what achievements did I unlock and what is known versus uncertain about their history?;
- are my saves protected?;
- is this game's tracking healthy?;
- what patterns exist in my own gaming history?

GameHours is deliberately **not** trying to become a storefront, social network or universal launcher.

That distinction matters architecturally. Tracking identity and measured history are authoritative local data. Covers, rarity, descriptions and other enrichments are replaceable metadata. Optional integrations must never become prerequisites for recording playtime.

---

# 2. Cross-cutting design rules

## 2.1 Keep authoritative data separate from presentation/enrichment

A game identity used by tracking should stay small and stable. User organization and optional metadata belong in separate models.

Conceptually:

```text
TrackedGame identity
      |
      +--> measured sessions / evidence / achievements
      |
      +--> LibraryPreferences        user-owned, durable
      |
      +--> MetadataSnapshot          replaceable/cacheable
      |
      +--> GameHealthSnapshot        derived
      |
      +--> SaveSafetyState           derived + operation history
```

A metadata-provider outage must not affect playtime tracking. Changing a tag must not change executable identity. A failed save backup must not mutate a measured session.

## 2.2 Local-first means useful offline, not "never use the network"

Network enrichment is allowed when it materially improves the product, but it must be:

- optional;
- explicit in Settings;
- cached locally;
- non-blocking for the core UX;
- privacy-minimal;
- replaceable behind provider boundaries.

## 2.3 Provenance beats false certainty

GameHours already distinguishes measured playtime from reconstructed evidence and complete achievement catalogues from partial state. New features must preserve that principle.

Examples:

- rarity from an online provider is enrichment, not authoritative unlock state;
- a save backup can be `verified`, `failed`, `unknown` or `not configured`; do not present "protected" merely because a folder exists;
- focus ratios are shown only over intervals where focus coverage is known;
- historical achievement timestamps remain explicitly uncertain when the source does not preserve them.

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

The default view remains simple:

```text
[ Buscar juegos... ]   [Todos] [Favoritos] [Jugando] [Completados] [Más]

AHORA
  current games...

BIBLIOTECA
  active first -> recently played -> older
```

Important behavior:

- an empty query preserves the current active/recent ordering;
- typing filters immediately without blocking the UI;
- filters combine predictably;
- hidden/archive does not delete the game or its history;
- favorites influence filtering/presentation but do not silently reorder every view unless the chosen sort asks for it;
- user state is editable from the game detail and, where useful, a compact context action.

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

**Library 2.0A — preferences + search**

- favorite;
- hidden/archive;
- completion status;
- search;
- quick filters;
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

Library 2.0 is successful when a user with a large local history can find and organize a game quickly without changing or endangering the tracking identity underneath it.

---

# 4. Game Health and supportability

## 4.1 User problem

GameHours already knows a great deal about why a game is or is not being tracked: executable resolution, learned mappings, source health, achievement state, historical evidence and reconciliation. Much of that knowledge currently lives in diagnostics/logs or requires understanding implementation details.

The product should answer a simpler question:

> "Is GameHours tracking this game correctly, and if not, what exactly needs attention?"

## 4.2 Health model

A per-game snapshot should be derived from existing services, not from a new scanner.

Conceptually:

```text
GameHealthSnapshot
- OverallState: Ready | NeedsAttention | NotTracking
- Summary
- Checks[]
- AvailableActions[]
- TechnicalDetails
- ObservedAtUtc
```

Each check should have its own state and explanation, for example:

- game identity;
- executable mapping;
- currently observed/tracked process;
- last measured session;
- historical recovery availability;
- achievement catalogue/state source;
- notification transport;
- Save Safety state when enabled.

The overall state should be a deterministic reduction of checks, not a pile of UI-specific conditions.

## 4.3 Simple versus advanced information

Default presentation should say things such as:

```text
CORRECTO
GameHours reconoce el ejecutable y está siguiendo el juego.

✓ Ejecutable reconocido
✓ Seguimiento activo
✓ Logros locales disponibles
✓ Guardados protegidos hace 12 min
```

Advanced details can expose exact executable path, resolver source, AppID/source identity, timestamps and diagnostic codes.

This distinction is inspired by Achievement Watcher Next's Game Health UX, but GameHours should implement its own health model from its own services.

## 4.4 Diagnosis and repair must be separate

The first Game Health PR should be **read-only**.

Actions come afterwards and should invoke existing authoritative workflows:

- resolve/associate executable;
- open Pendientes;
- open install folder;
- rescan achievement sources;
- run existing confirmed GSE catalogue preparation;
- send a test notification;
- copy/export technical diagnostics.

A repair should never duplicate the business logic already owned by another service.

## 4.5 Diagnostic bundle

Before a public beta, GameHours should create a support ZIP with centrally enforced redaction.

Candidate contents:

- GameHours version/build/channel;
- Windows/.NET/runtime summary;
- schema version;
- provider/source health summary;
- selected logs;
- safe configuration;
- optional user problem description.

Explicitly exclude or redact:

- usernames;
- tokens/secrets;
- raw SRUM or unrelated registry content;
- unrelated personal files;
- full machine paths unless a path has been transformed into a safe diagnostic representation.

Playnite's MIT `Diagnostic.CreateDiagPackage` is a useful implementation reference for packaging flow. If substantial code is adapted, preserve attribution as required by its license.

## 4.6 Delivery slices

**Game Health 1 — read-only model + panel**  
**Game Health 2 — contextual safe actions**  
**Diagnostics 1 — privacy-minimal support bundle**  
**Diagnostics 2 — direct linking from errors/empty states to the relevant health check**

## 4.7 Definition of done

A non-technical user should be able to tell whether tracking is healthy and what to do next without reading logs; an advanced user should still be able to inspect precise technical evidence.

---

# 5. Achievements 2.0

## 5.1 User problem

The underlying achievement architecture already handles local sources, catalogue completeness, unlock state, confidence and session-scoped notifications. The next value is mainly product quality and broader source/enrichment coverage, not another rewrite of the core model.

## 5.2 Modern Windows notifications

The unlock event should remain transport-neutral. Replace the legacy tray balloon with a modern Windows notification transport behind the existing boundary.

Desired result:

```text
🏆 Logro desbloqueado
Big Walk
Click the Button
```

with artwork where already available, without coupling achievement detection to WPF notification APIs.

Before implementation, verify the current recommended Microsoft path for unpackaged/Velopack WPF apps and test activation/identity behavior on the installed build.

## 5.3 Achievement browsing

Improve the detail experience with:

- locked/unlocked/hidden/progress filters;
- progress values only when the source provides them;
- clear completion milestones;
- stable ordering;
- good empty/loading/source-incomplete states;
- no punctuation tricks that imply extra unlocks or false totals.

## 5.4 Optional rarity enrichment

Rarity belongs behind an optional provider boundary.

It may provide:

- global unlock percentage;
- rarity tier;
- richer official metadata/artwork.

It must not change the authoritative local unlock state. If the provider is offline or unavailable, achievement tracking remains fully functional.

SuccessStory's MIT models are useful references for rarity/presentation data, but GameHours already has stronger evidence/provenance semantics and should preserve them.

## 5.5 Screenshot souvenirs

A screenshot captured around an unlock could become a distinctive feature, but it stays deferred until a prototype proves:

- reliable capture for common windowed/borderless/fullscreen cases;
- acceptable performance;
- safe behavior with protected content/anti-cheat;
- predictable storage/privacy controls.

No hooking/overlay dependency should be introduced merely to enable souvenirs.

## 5.6 Delivery slices

**Achievements 2.0A — modern Windows notification transport**  
**Achievements 2.0B — filters/progress/completion UX**  
**Achievements 2.0C — optional rarity provider**  
**Achievements 2.0D — screenshot prototype only if justified**

## 5.7 Definition of done

Achievements should feel native and polished while preserving the current local-first, evidence-aware behavior when no online enrichment is available.

---

# 6. Save Safety — integrated Ludusavi engine

## 6.1 User outcome

GameHours should be able to protect a user's game saves without asking the user to discover save paths manually and without requiring a separate save-manager installation.

The intended experience is:

```text
Gothic 1 Remake
Guardados
✓ Protegidos
Última copia: hoy, 23:14
3 versiones conservadas

[Crear copia ahora]   [Ver copias]
```

Later, after restore safety is proven:

```text
[Restaurar...]
```

Automatic backup after a measured session is opt-in and should feel like a native GameHours capability.

## 6.2 Why reuse Ludusavi instead of writing a save engine from zero

Ludusavi already solves the difficult, maintenance-heavy part:

- game-to-save-layout knowledge;
- file path expansion;
- Windows Registry save locations;
- store identifiers;
- scanning;
- backup/restore logic;
- retention/version handling;
- a community-maintained manifest.

The upstream project is MIT licensed. Its current Rust package also separates the GUI/CLI `app` feature from the library crate, and `src/lib.rs` explicitly exposes internal modules such as `scan`, `path`, `resource`, `api`, `serialization` and `report`.

That makes source-level reuse technically realistic.

There is an important caveat: Ludusavi's own `lib.rs` warns that the library API is currently unstable and many internals were not originally designed as a stable public library. GameHours must therefore isolate that instability behind a small boundary it owns.

## 6.3 Explicitly rejected default approaches

### Require the user to install Ludusavi separately

Rejected as the preferred product architecture.

It would be easy to implement, but it creates avoidable UX and support burden:

- a second application to install/update/configure;
- path/version detection;
- mismatched configurations;
- unclear responsibility when backup fails;
- GameHours appears incomplete without another app.

A separately installed Ludusavi may remain a useful development/debug compatibility path, but it is not the intended end-user requirement.

### Port Ludusavi's Rust engine to C#

Rejected.

A port would immediately fork years of path/scanning/backup behavior. Every upstream fix would need to be reinterpreted and manually reimplemented. The apparent convenience of "all C#" would create much greater long-term maintenance cost.

### Vendor the whole Ludusavi application

Rejected.

GameHours does not need Ludusavi's GUI, CLI presentation, themes or unrelated application lifecycle. Pulling the full app into the product would add dependency/build/update surface without adding user value.

### Expose Ludusavi Rust types directly throughout .NET via FFI

Not preferred for the first implementation.

Direct FFI can work, but it creates native ABI, ownership, error-marshalling and unsafe-boundary complexity. It would also expose an explicitly unstable upstream API too widely.

## 6.4 Preferred architecture: bundled GameHours SaveEngine

Create a small GameHours-owned native helper, conceptually:

```text
GameHours.Desktop / Core (.NET)
           |
           | versioned JSON request/response
           v
GameHours.SaveEngine (small Rust helper, shipped with GameHours)
           |
           | pinned source/library dependency
           v
Ludusavi core (MIT, default app feature disabled)
           |
           +--> scan/path/registry/backup/restore
           +--> Ludusavi manifest data
```

The helper is **part of GameHours distribution**. The user installs one product and does not manage `ludusavi.exe` separately.

Why prefer a small helper process over direct FFI initially:

- process isolation contains crashes/panics;
- JSON gives a language-neutral, testable contract;
- the unstable Rust API is confined to one small component;
- no native pointer/object lifetime crosses into .NET;
- it can be versioned and smoke-tested independently;
- a helper failure cannot corrupt the WPF process state;
- future replacement of the underlying engine is possible without changing the Desktop contract.

Start with one-shot operations rather than a permanent daemon. A backup does not require another always-running process.

## 6.5 Proposed GameHours-owned contract

The exact schema is an implementation decision, but the boundary should stay small and versioned.

Candidate operations:

```text
GetCapabilities
ResolveGameSaveData
PreviewBackup
CreateBackup
ListBackups
PreviewRestore
RestoreBackup
```

Every request/response should include a protocol version and machine-readable error category. GameHours should distinguish at least:

- unsupported game;
- ambiguous game mapping;
- no save data found;
- permission/access failure;
- backup storage failure;
- incompatible engine/protocol version;
- cancelled/timeout;
- internal engine failure.

Do not parse human-readable console output.

## 6.6 Game identity mapping

Prefer stable IDs already known by GameHours:

1. Steam AppID when present;
2. GOG/store identity where available;
3. other stable platform IDs supported by the engine;
4. normalized title only when unambiguous;
5. explicit user mapping when ambiguity remains.

A title guess must never silently back up another game's data.

## 6.7 Responsibility split

**Ludusavi-derived engine owns:**

- understanding save locations;
- resolving manifest paths/registry locations;
- scanning save files;
- creating/restoring backup content;
- backup-format details that belong to the engine.

**GameHours owns:**

- when to request a backup;
- mapping GameHours game identity to the engine request;
- UI and user consent;
- operation scheduling/cancellation;
- displaying backup health/history;
- policy such as automatic-after-session on/off;
- persistence of GameHours-side operation summaries;
- protecting playtime tracking from backup failures.

This boundary avoids duplicating Ludusavi while keeping product behavior under GameHours control.

## 6.8 Session integration

GameHours already knows when a measured session completes. Reuse that lifecycle:

```text
Measured SessionCompleted
        |
        v
SaveBackupCoordinator
        |
        +--> disabled? -> no work
        +--> backup already running? -> coalesce/skip safely
        |
        v
GameHours.SaveEngine CreateBackup
        |
        v
record operation result + refresh UI
```

No new process scanner and no second game-running detector.

The save operation runs outside the UI-critical tracking path. Failure must not modify session duration, game identity or achievement state.

## 6.9 Restore safety

Restore is more dangerous than backup and should ship later.

Requirements before enabling restore:

- preview exactly what would change;
- explicit user confirmation;
- create a pre-restore safety backup where possible;
- never overwrite while the game is known to be running unless the engine/game-specific evidence says it is safe;
- surface conflicts/downgrades rather than guessing;
- report partial failure precisely;
- keep recovery information if a restore does not complete.

## 6.10 Manifest/update strategy

Do not casually fork the Ludusavi manifest into a GameHours-specific format.

During the first implementation spike, prove the cleanest way for the integrated engine to consume/upkeep upstream manifest data while retaining offline usefulness. The chosen mechanism must have:

- a known upstream revision/source;
- reproducible builds;
- a local cached/pinned fallback;
- an update path that cannot silently replace authoritative GameHours data;
- clear attribution.

If the manifest is redistributed with GameHours, its MIT license/copyright notice must be included.

## 6.11 Licensing and provenance

Once Ludusavi code is actually incorporated into the build/distribution:

- pin the exact upstream revision/version;
- preserve Matthew T. Kennerly's MIT copyright/license notice;
- add/update `THIRD-PARTY-NOTICES.md`;
- keep an upstream/revision record close to the native component;
- document local changes if any source is vendored or patched;
- include the same discipline for `ludusavi-manifest` if redistributed.

The roadmap/reference documents alone do not require a third-party notice because they do not distribute upstream code.

## 6.12 Build, security and performance requirements

The native component must not become an opaque exception to GameHours quality rules.

Before shipping:

- reproducible/pinned Cargo dependencies;
- Release build in CI;
- tests for the JSON protocol;
- smoke tests using temporary save trees;
- cancellation/timeout behavior;
- path traversal/unsafe destination review;
- no shell command construction from untrusted strings;
- package contents and license notices verified in CI;
- Windows artifact signing strategy includes the helper binary;
- backup work never runs on the WPF UI thread;
- no persistent helper process unless measurement proves it is needed.

## 6.13 Delivery slices

**Save Safety 1 — engine feasibility/bridge**

- add the smallest Rust helper project;
- pin a Ludusavi revision with application feature disabled where viable;
- `GetCapabilities` + one read-only save-data resolution/preview path;
- protocol tests;
- packaging/licensing proof;
- no automatic backups yet.

This slice answers the highest-risk architectural question before building UI around it.

**Save Safety 2 — manual preview + backup**

- GameHours game mapping;
- preview detected save data;
- `Crear copia ahora`;
- operation result persisted/displayed;
- clear unsupported/ambiguous/error states.

**Save Safety 3 — automatic after measured session**

- opt-in setting;
- hook existing `SessionCompleted` lifecycle;
- serialization/coalescing per game;
- non-blocking background execution;
- visible last-success/last-failure state.

**Save Safety 4 — backup history + retention UX**

- list versions;
- storage usage;
- retention controls that map cleanly onto the engine;
- no duplicated backup index if the engine already owns that data.

**Save Safety 5 — restore**

- preview;
- safety backup;
- explicit confirmation;
- conflict/downgrade handling;
- robust failure/recovery UX.

## 6.14 Definition of done

Save Safety is complete when GameHours can natively protect supported saves with no separate application installation, while clearly attributing/reusing Ludusavi's MIT engine and keeping all unstable/native details behind a small GameHours-owned boundary.

---

# 7. Platform/source expansion

## 7.1 Separate three different capabilities

For every platform, distinguish:

1. **Library discovery** — know that a game is installed and its identity/path;
2. **Playtime tracking** — resolve its real processes through the existing tracker;
3. **Achievements** — read an achievement catalogue/state if a reliable local/optional source exists.

Do not block discovery/tracking on achievement support.

## 7.2 Proposed order

1. Xbox / Microsoft Store / Game Pass;
2. Ubisoft Connect;
3. EA Desktop;
4. Amazon Games / Battle.net when reliable local evidence is characterized;
5. selected emulators only when a real use case justifies them.

## 7.3 Per-platform research template

Before writing production code:

- install/inspect the real Windows client where possible;
- identify official APIs/docs first;
- characterize manifests/databases/packages on disk;
- record exact evidence in a short `docs/` format note;
- inspect mature OSS adapters for leads;
- review license before adapting code;
- define what can be supported offline;
- identify launcher/helper processes that must never count as game time;
- add fixtures/tests from sanitized representative layouts;
- validate on a real installed game before claiming support.

## 7.4 Architecture rule

A new platform adapter feeds existing identity/discovery layers. It must not create a separate platform-specific process tracker.

Conceptually:

```text
Xbox/Ubisoft/EA local metadata
        |
        v
DiscoveredGame / store identity
        |
        v
existing Windows resolver + tracker
```

## 7.5 Definition of done

A platform is supported only for the capabilities actually verified. Documentation/UI should be able to say, for example, "installed-game discovery + playtime tracking supported; achievements not yet supported" rather than presenting one vague support flag.

---

# 8. Insights 2.0

## 8.1 User problem

GameHours already collects data that normal launchers often collapse into a single total. The value now is to make that history explorable without inventing precision.

## 8.2 Candidate insights

Per game and globally:

- sessions per day/week/month;
- average and median session duration;
- longest sessions;
- day-of-week distribution;
- time-of-day distribution;
- calendar heatmap;
- executed/focused/estimated-active time;
- focus/active ratio where coverage is known;
- recent versus lifetime trends;
- achievement unlock activity;
- streaks only if defined carefully and not gamified misleadingly.

## 8.3 Coverage-aware metrics

A ratio such as active/executed time is meaningful only over periods where both measurements exist.

Do not compute:

```text
all-time active / all-time executed
```

if active telemetry only started halfway through the history. Instead compute over the intersection of known coverage and display that scope.

Historical SRUM evidence should stay separate from measured daily timelines because it may not preserve exact per-session/day structure.

## 8.4 Query architecture

Prefer SQLite/bulk read models for larger aggregates rather than loading every session into WPF repeatedly.

Add only indexes/summary tables shown necessary by query plans/measurements. Keep raw authoritative sessions; derived aggregates should be reproducible.

## 8.5 UX

Insights should answer a question, not become a wall of charts.

A good hierarchy:

```text
Esta semana
12 h 40 min · 6 sesiones

Tus hábitos
Sábado es tu día más jugado
Sesión mediana: 1 h 18 min
Horario habitual: 21:00–00:00

Actividad real
Ejecutado 10 h 20 min
En primer plano 9 h 04 min
Activo estimado 8 h 31 min
Cobertura: últimos 30 días
```

Drill-down should lead to the sessions behind the aggregate.

## 8.6 Delivery slices

**Insights 2.0A — session distribution + median/longest**  
**Insights 2.0B — day/time heatmaps**  
**Insights 2.0C — focus/active coverage-aware metrics**  
**Insights 2.0D — achievement activity + drill-down polish**

## 8.7 Definition of done

The statistics screen should reveal useful patterns that cannot be obtained from a single launcher playtime counter while remaining faithful to measurement coverage/confidence.

---

# 9. First-run, help and beta UX

## 9.1 User problem

A technically sophisticated tracker can still feel broken if the user does not understand what it detected, what it is waiting for or why a game is absent.

The beta experience must explain itself without requiring knowledge of SRUM, resolver confidence or launcher manifests.

## 9.2 First-run principles

Do not create a long mandatory wizard.

The default path should be roughly:

```text
Bienvenido a GameHours
Tu historial se guarda localmente.

Detectado en este PC
✓ Steam
✓ Epic
✓ GOG

GameHours empezará a medir automáticamente los juegos reconocidos.
[Empezar]
```

Advanced choices belong in Settings.

## 9.3 Contextual help

Prefer help at the point of failure:

- no games -> explain discovery and offer rescan/manual add;
- unresolved executable -> link to Pendientes;
- achievement source incomplete -> explain exactly what is known;
- Save Safety unsupported -> explain unsupported/ambiguous rather than a generic error;
- updater failure -> provide recovery action/documentation.

Game Health should become the common destination for per-game troubleshooting.

## 9.4 Accessibility and localization

Before multiplying UI strings further:

- establish localization resource structure;
- audit keyboard navigation/focus order;
- ensure status is not communicated only by color;
- verify scaling/DPI and text clipping;
- test screen-reader names for primary controls where practical;
- respect reduced-motion expectations for nonessential animation.

## 9.5 Diagnostic support

The privacy-minimal diagnostic bundle belongs in this beta track even though its implementation is grouped with Game Health. Supportability is part of UX.

## 9.6 Definition of done

A new user can install GameHours, understand what it will do, see what was detected and recover from common problems without reading repository documentation.

---

# 10. Distribution and trust

## 10.1 Objective

Convert the already-implemented packaging/update foundation into a trustworthy public release path.

## 10.2 Required gates

- operational Azure Artifact Signing/OIDC configuration;
- real signed release from `main`;
- Authenticode verification of every executable binary shipped, including future native helpers such as `GameHours.SaveEngine`;
- clean installation test;
- signed previous-version -> current-version update test;
- rollback/recovery validation;
- package-content verification;
- published checksums/attestation as already designed;
- SmartScreen observation with the real signed installer;
- clear install/update/uninstall/data-location documentation.

## 10.3 Dependency and third-party visibility

As GameHours begins to ship third-party/native components, release packaging must verify:

- required license/notice files are present;
- no source/build secrets are packaged;
- helper binaries correspond to the pinned reviewed source revision;
- SBOM/third-party inventory can be generated or audited reproducibly.

## 10.4 Definition of done

A public build can be downloaded, its publisher/signature verified, installed and updated predictably, and the user knows where local data lives and how to recover it.

---

# 11. Cross-cutting performance track

Performance is continuous and evidence-driven, not a separate rewrite phase.

Measure when relevant:

- startup/time-to-interactive;
- idle/tray CPU;
- active tracking CPU;
- UI frame/interaction responsiveness;
- Private Memory / Working Set;
- managed allocation rate/GC when investigating memory;
- SQLite query counts/durations;
- image-cache size/hit behavior;
- native SaveEngine startup/operation overhead once it exists;
- network work and cache misses for metadata providers.

Preferred optimization order:

1. eliminate unnecessary work;
2. avoid repeated I/O/network calls;
3. batch database reads;
4. lazy-load expensive views/artwork;
5. virtualize long UI lists;
6. bound caches;
7. only then consider lower-level tuning.

Never use forced GC/working-set trimming as cosmetic optimization.

---

# 12. Deliberately deferred capabilities

Reconsider later only after internal contracts have matured:

- public plugin/provider SDK;
- local automation/query API;
- optional cross-device/cloud sync on top of the neutral sync boundary;
- richer theming;
- screenshot souvenirs after a safe capture prototype.

Still excluded unless product direction explicitly changes:

- social network / friends / feeds / public profiles;
- game purchasing/storefront;
- game installation/uninstallation management;
- replacing Steam/Playnite/Heroic as a general launcher.

---

# 13. Recommended implementation order

The roadmap priority remains product-driven, but implementation should keep risk contained.

Recommended near-term sequence:

1. **Library 2.0A** — preferences + lightweight search/filtering;
2. **Game Health 1** — read-only health snapshot/panel;
3. **Diagnostics 1** — privacy-minimal support bundle;
4. **Achievements 2.0A** — modern Windows notification transport;
5. **Save Safety 1** — integrated `GameHours.SaveEngine` feasibility/bridge using pinned Ludusavi core;
6. **Library 2.0C** — metadata boundary after basic organization is stable;
7. **Save Safety 2** — manual preview/backup after the native bridge is proven;
8. then select Insights/platform work from measured user/product value rather than executing every phase mechanically.

Each slice should update this detailed document only when evidence changes the intended architecture or product outcome. Avoid turning the roadmap into a changelog; completed implementation evidence belongs in PRs and validation documents.
