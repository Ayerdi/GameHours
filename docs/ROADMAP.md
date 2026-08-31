# GameHours product roadmap — post-foundation

**Status:** active product roadmap after the merge of PR #1 (`desktop-foundation`).

**Baseline:** `main` at `16f9422a1d7195187017c09da76e76bfd3a9b0f4` when this roadmap was written.

This document defines **where the product goes next**. Historical engineering evidence, one-off validation gates and completed foundation work remain in the execution/validation documents; they must not be mistaken for forward product priorities.

The roadmap is intentionally opinionated: GameHours should become the **reliable personal history of the user's videogames**, not another storefront or social launcher.

---

## 1. Product identity and non-goals

GameHours remains:

- local-first;
- launcher-independent for authoritative playtime tracking;
- privacy-minimal by default;
- useful without accounts, cloud services or Internet access;
- explicit about measured vs reconstructed/estimated information;
- focused on the user's own game history: sessions, activity, achievements, saves and insights.

Explicit non-goals:

- **no social network, friends, feeds or profiles**;
- **no game installation/uninstallation or storefront management**;
- no attempt to replace Steam, Playnite, Heroic or other launchers;
- no mandatory cloud/account dependency;
- no in-game overlay in the near-term roadmap; modern Windows notifications come first;
- no public plugin SDK until GameHours' internal provider contracts have matured through several real integrations.

Optional network enrichment is allowed only behind an explicit provider boundary, with local caching and a useful offline fallback.

---

## 2. Engineering rules for roadmap work

Every roadmap item must ship as a small, reviewable vertical slice. Do not recreate another oversized foundation branch.

Before implementation:

1. check whether GameHours already owns the required primitive;
2. research official platform documentation and mature projects listed in `REFERENCE-PROJECTS.md`;
3. prefer platform/framework functionality and maintained external tools over reimplementing solved problems;
4. copy third-party source only when it has a clear maintenance advantage over an independent implementation and the license boundary is explicitly handled;
5. validate runtime/persistence changes with focused tests, full applicable tests, Release build and CI for the exact SHA.

### Keep tracking identity small

Playnite demonstrates the value of rich library metadata, but GameHours must **not** turn `TrackedGame` into a giant all-purpose object. Tracking identity (`GameId`, canonical title and executable relationships) should remain stable and minimal. User-facing library preferences and enrichments should live in dedicated persistence/read models so metadata failures cannot compromise authoritative tracking.

---

# 3. Priority 1 — Library 2.0

**Goal:** make the library pleasant and efficient once it contains tens or hundreds of games.

### 3.1 First slice: organization and search

Add, in small steps:

- search by title;
- favorite;
- hidden/archive state;
- completion status such as `Pendiente`, `Jugando`, `Completado`, `Abandonado`;
- optional user tags;
- quick filters using those fields plus currently-running/recently-played;
- preserve the current default ordering by active game first and recent activity afterwards.

Search should stay lightweight. A good starting behavior is:

- empty query -> recent games;
- normalized case/diacritic-insensitive word matching;
- prefix/acronym preference;
- fuzzy ranking only if simple matching proves insufficient.

Do not add a search-engine dependency for a local library of this scale.

### 3.2 Second slice: optional metadata enrichment

Add a provider boundary for optional metadata such as:

- cover/background artwork;
- developer/publisher;
- release date;
- genres;
- short description;
- platform/store identity where useful.

Requirements:

- local information wins when authoritative;
- online providers are optional;
- downloaded metadata/artwork is cached locally;
- a provider failure never blocks the Library;
- network access is visible/configurable;
- metadata does not leak into the tracking core.

**Primary reference:** Playnite's `Game` model, search behavior and metadata-provider separation. See `REFERENCE-PROJECTS.md`.

---

# 4. Priority 2 — Game Health and supportability

**Goal:** turn GameHours' internal diagnostics into understandable per-game product UX.

Each game should expose one compact health state:

- **Correcto** — tracking and relevant sources are healthy;
- **Necesita atención** — usable, but one relevant capability is incomplete;
- **No se está siguiendo** — an essential identity/tracking requirement is missing.

The panel should explain the cause in plain language and expose only checks relevant to that game, for example:

- executable / game identity;
- current tracking state;
- discovery source;
- historical-recovery availability;
- achievement catalogue/state source and source health;
- optional save-backup integration state;
- last successful observation/reconciliation.

Advanced details may expose exact technical values, but Simple mode should describe outcomes rather than implementation internals.

### Guided actions

Repairs/actions must be contextual and conservative, such as:

- resolve/associate an executable;
- open the install folder;
- rescan achievements;
- prepare a GSE/Goldberg catalogue only through the existing confirmed write path;
- open Pendientes for an unresolved executable;
- test the notification transport;
- open/copy technical diagnostics.

Game Health must reuse the **existing resolver, source-health and diagnostics data**. It must not create a second process scanner or parallel source of truth.

### Diagnostic bundle

Add a one-click privacy-minimal diagnostic ZIP before public beta. Candidate contents:

- GameHours version and build information;
- Windows/.NET/runtime summary required for troubleshooting;
- schema/version and provider-health summary;
- relevant logs;
- non-sensitive configuration;
- optional user problem description.

Mandatory redaction/exclusion:

- Windows usernames;
- raw SRUM/registry data;
- secrets/tokens;
- full machine paths unless explicitly approved or safely redacted;
- unrelated personal files.

**Primary references:** Achievement Watcher Next's Game Health product behavior (LGPL: behavior reference only) and Playnite's MIT `Diagnostic.CreateDiagPackage` implementation pattern.

---

# 5. Priority 3 — Achievements 2.0

**Goal:** make the achievement system feel like a first-class product rather than only a compatibility layer.

### Near-term

- replace legacy tray-balloon achievement notifications with a modern Windows notification transport while keeping detection/persistence transport-neutral;
- improve progress-achievement presentation where a source provides progress;
- filters for locked/unlocked/hidden/progress;
- clearer 100% completion presentation and completion milestones;
- preserve the current timestamp-confidence wording: historical uncertainty must never become fake precision.

### Optional online enrichment

A separate opt-in metadata provider may add:

- global unlock percentage / rarity;
- rarity tiers;
- richer official artwork when local metadata is insufficient.

Rarity must never become a requirement for local achievement tracking.

### Later polish

- optional screenshot "souvenir" around a newly unlocked achievement, only after researching a robust Windows capture path and its interaction with fullscreen games, protected content and performance;
- no in-game overlay until there is strong evidence that its value justifies the hooking/rendering/anti-cheat complexity.

**References:** SuccessStory (MIT) for completion/rarity-oriented models; Achievement Watcher / AW Next (LGPL) for notification and UX behavior. Do not copy LGPL implementation into GameHours.

---

# 6. Priority 4 — Save Safety through Ludusavi

**Goal:** protect game saves without building a second save-manager database from scratch.

The preferred architecture is an **optional external Ludusavi adapter**, not a GameHours-owned save manifest.

Why:

- Ludusavi already covers a very large game catalogue and file/registry save layouts;
- it exposes machine-readable JSON through `--api` and explicit schemas;
- it can resolve games by stable store IDs (`find --steam-id`, GOG ID, etc.);
- it supports preview, backup, restore, retention and downgrade/conflict handling.

### First slice

- detect a user-installed Ludusavi executable/configuration;
- map a GameHours game to Ludusavi using stable IDs first and title fallback only when unambiguous;
- expose `Preview backup` / `Back up now` from the game detail;
- parse only machine-readable output;
- never silently guess on an ambiguous title.

### Second slice

Opt-in automatic backup after a **measured `SessionCompleted`** event:

- run outside the UI/main tracking path;
- one operation per game at a time;
- clear success/failure state in the UI;
- failure cannot modify GameHours playtime/session state;
- do not block application shutdown indefinitely.

### Later

- manual restore from GameHours;
- optional restore-before-play only after explicit conflict/downgrade UX is proven safe;
- optional periodic backup during very long sessions only if there is real demand.

Do **not** vendor Ludusavi's manifest initially. Prefer its maintained CLI boundary and let Ludusavi own its data lifecycle.

**Primary references:** `mtkennerly/ludusavi` and the MIT `mtkennerly/ludusavi-playnite` lifecycle integration.

---

# 7. Priority 5 — Platform/source expansion

**Goal:** recognize more real PC libraries while keeping tracking independent from launchers.

Discovery/tracking support and achievement support are separate capabilities. A platform does not need complete achievement support before GameHours can discover and track its games.

Proposed order for research slices:

1. **Xbox / Microsoft Store / Game Pass**;
2. **Ubisoft Connect**;
3. **EA Desktop**;
4. **Amazon Games / Battle.net** where reliable local discovery data exists;
5. selected console emulators only when a real user case justifies them.

Rules for every platform PR:

- investigate official/local storage first;
- characterize actual Windows layouts before implementation;
- one platform/source per PR where practical;
- no credential scraping;
- no claim of support without real-layout evidence;
- platform adapters cannot become a requirement for the core tracker;
- reuse existing process-resolution and game-identity layers instead of adding platform-specific trackers.

Achievement Watcher Next is a useful behavioral/source-matrix reference for Xbox, Ubisoft, EA and emulators, but its LGPL source is not to be copied into the MIT GameHours codebase.

---

# 8. Priority 6 — Insights 2.0

**Goal:** exploit information GameHours already owns better than generic launcher counters do.

Candidate insights:

- sessions per day/week/month;
- session-length distribution and average/median session;
- longest sessions;
- day-of-week and time-of-day habits;
- calendar heatmap;
- executed vs focused vs estimated-active time;
- focus/active ratio where coverage is known;
- recent-vs-lifetime trends;
- achievement unlock activity over time;
- per-game drill-down from aggregate statistics.

Rules:

- keep measured and historical/estimated data visually distinguishable;
- never manufacture daily precision for SRUM evidence;
- aggregate in SQLite/bulk read models when datasets grow instead of materializing large histories repeatedly in WPF;
- measure before adding caches or complex query layers.

**Reference:** ActivityWatch's separation of collection, event storage and query/visualization is useful conceptually. GameHours should not copy its MPL source or expand into browser/window-title surveillance.

---

# 9. Priority 7 — First-run, help and beta UX

**Goal:** make GameHours understandable without requiring the user to understand its architecture.

Before public beta:

- first-run onboarding that explains local-first behavior and what will be detected;
- show which local stores/sources were found;
- explain optional network metadata separately from local tracking;
- contextual empty/error states;
- direct links from errors to the relevant Game Health/Pendientes action;
- concise in-app explanations for measured vs historical/estimated time;
- one-click diagnostic bundle;
- installation/update/recovery help;
- keyboard/focus/accessibility review of main flows;
- localization architecture before multiplying user-facing strings significantly.

Do not create a long mandatory wizard. A successful default path should be only a few decisions, with advanced choices deferred to Settings.

---

# 10. Priority 8 — Distribution and trust

Most supply-chain groundwork is already present. The forward gate is operational rather than another broad security refactor.

Before broad public distribution:

- provision the planned Azure Artifact Signing resources/OIDC permissions;
- produce a real signed release from `main`;
- validate Authenticode on produced artifacts;
- verify clean install and update from a previous signed version;
- verify recovery/rollback path;
- evaluate SmartScreen behavior with the signed installer;
- publish concise installation, update, uninstall and local-data documentation.

Locked restore, Dependabot, CodeQL and SHA-pinned Actions should be treated as existing baseline unless a fresh audit proves a gap.

---

# 11. Cross-cutting performance track

Performance work is evidence-driven and is **not** a standalone excuse for speculative refactors.

Continue to measure:

- startup latency and time-to-interactive;
- UI responsiveness;
- CPU while idle/tray/game-active;
- Private Memory / Working Set;
- GC heap/allocation rate when investigating memory;
- long-list rendering and image-cache behavior;
- repeated disk/network work that can be avoided.

Prefer eliminating work, batching queries and lazy-loading views before GC tuning. Do not add periodic `GC.Collect`, working-set trimming or similar cosmetic memory hacks.

---

# 12. Later / deliberately deferred

Reconsider only after the above contracts have matured:

- public provider/plugin SDK;
- local automation/query API;
- optional cross-device/cloud sync built on the existing neutral boundary;
- richer theme customization;
- achievement screenshot souvenirs if the capture prototype is robust.

Still excluded unless product direction changes explicitly:

- social/friends/profiles;
- game store/storefront features;
- game installation/uninstallation management.

---

# 13. Recommended next PR sequence

To keep the post-foundation workflow small and reviewable, start with:

1. **Library 2.0A — library preferences + search**: favorite/hidden/completion status and lightweight search/filtering, without online metadata yet.
2. **Game Health 1 — read-only health model**: compute and display status/checks using existing diagnostics, with no repair writes in the first PR.
3. **Diagnostic bundle 1 — privacy-minimal export**: version/provider/log bundle with explicit redaction tests.
4. **Achievements 2.0A — modern Windows notification transport** behind the existing neutral unlock event.
5. **Ludusavi 1 — manual preview/backup adapter** before any automatic session hook.

Each of these should be independently mergeable. Metadata enrichment, automatic save backup and new platforms follow only after their respective first slices are stable.
