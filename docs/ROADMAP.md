# GameHours product roadmap — post-foundation

**Status:** active product roadmap after the merge of the desktop foundation.

This document defines **priority and direction**. The deeper product/architecture rationale, delivery slices, risks and definition of done for every area live in [`ROADMAP-DETAILS.md`](ROADMAP-DETAILS.md).

Historical engineering evidence, one-off validation gates and completed foundation work remain in the execution/validation documents; they must not be mistaken for forward product priorities.

The roadmap is intentionally opinionated: GameHours should become the **reliable personal history of the user's videogames**, not another storefront, social network or general launcher.

---

# 1. Product identity and non-goals

GameHours remains:

- local-first;
- launcher-independent for authoritative playtime tracking;
- privacy-minimal by default;
- useful without accounts, cloud services or Internet access;
- explicit about measured vs reconstructed/estimated information;
- focused on the user's own game history: sessions, activity, achievements, saves and insights.

Explicit non-goals:

- **no social network, friends, feeds or public profiles**;
- **no game installation/uninstallation or storefront management**;
- no attempt to replace Steam, Playnite, Heroic or other launchers;
- no mandatory cloud/account dependency;
- no in-game overlay in the near-term roadmap; modern Windows notifications come first;
- no public plugin SDK until GameHours' internal provider contracts have matured through several real integrations.

Optional network enrichment is allowed only behind an explicit provider boundary, with local caching and a useful offline fallback.

---

# 2. Engineering rules for roadmap work

Every roadmap item must ship as a small, reviewable vertical slice. Do not recreate another oversized foundation branch.

Before implementation:

1. check whether GameHours already owns the required primitive;
2. research official platform documentation and mature projects listed in [`REFERENCE-PROJECTS.md`](REFERENCE-PROJECTS.md);
3. prefer platform/framework functionality and maintained external components over reimplementing solved problems;
4. reuse/adapt permissively licensed source when it clearly reduces maintenance, but isolate third-party instability behind GameHours-owned contracts;
5. preserve required attribution/license notices when third-party source or data is actually distributed;
6. validate runtime/persistence changes with focused tests, full applicable tests, Release build and CI for the exact SHA.

### Keep tracking identity small

Playnite demonstrates the value of rich library metadata, but GameHours must **not** turn `TrackedGame` into a giant all-purpose object. Tracking identity should remain stable and minimal. User-facing library preferences, metadata, health state and save-backup presentation should live in dedicated models/read models so failures in those areas cannot compromise authoritative tracking.

---

# 3. Priority 1 — Library 2.0

**Goal:** make the library pleasant and efficient once it contains tens or hundreds of games.

Near-term scope:

- search by title;
- favorite;
- hidden/archive state;
- completion status such as `Pendiente`, `Jugando`, `Completado`, `Abandonado`;
- optional user tags;
- quick filters;
- preserve active-game-first + recent-activity ordering by default.

Search starts deliberately simple: normalized text matching and deterministic ranking before any fuzzy/search-engine dependency.

Later, add optional metadata enrichment behind a provider/cache boundary:

- cover/background artwork;
- developer/publisher;
- release date;
- genres;
- short description;
- platform/store metadata where useful.

Metadata never becomes part of authoritative tracking identity.

**Primary reference:** Playnite's mature library/search/metadata separation.

See [`ROADMAP-DETAILS.md#3-library-20`](ROADMAP-DETAILS.md#3-library-20).

---

# 4. Priority 2 — Game Health and supportability

**Goal:** turn GameHours' existing resolver/source/diagnostic intelligence into understandable per-game UX.

Each game should expose one compact health state:

- **Correcto**;
- **Necesita atención**;
- **No se está siguiendo**.

Checks may cover:

- executable/game identity;
- live tracking;
- last measured observation;
- historical-recovery availability;
- achievement source/catalogue state;
- notification transport;
- Save Safety state once enabled.

The first implementation is read-only. Guided actions come afterwards and must reuse existing authoritative workflows rather than duplicate scanners/repair logic.

Before public beta, add a privacy-minimal one-click diagnostic bundle with centralized redaction.

**References:** Achievement Watcher Next's Game Health behavior (LGPL: behavioral reference only) and Playnite's MIT diagnostic-package pattern.

See [`ROADMAP-DETAILS.md#4-game-health-and-supportability`](ROADMAP-DETAILS.md#4-game-health-and-supportability).

---

# 5. Priority 3 — Achievements 2.0

**Goal:** make achievements feel like a first-class GameHours experience while preserving the existing evidence-aware local architecture.

Near-term:

- modern Windows notifications behind the existing neutral unlock event;
- filters for locked/unlocked/hidden/progress;
- improved progress presentation when a source supplies progress;
- stronger completion/100% UX;
- preserve historical timestamp uncertainty instead of inventing precision.

Optional later enrichment:

- global unlock percentage / rarity;
- rarity tiers;
- richer official metadata/artwork.

Rarity remains optional and never controls local unlock truth.

Later experiment only if justified:

- screenshot "souvenir" around a new unlock.

No in-game overlay until its hooking/rendering/anti-cheat cost is demonstrably worth it.

**References:** SuccessStory (MIT) and Achievement Watcher/AW Next (LGPL behavior only).

See [`ROADMAP-DETAILS.md#5-achievements-20`](ROADMAP-DETAILS.md#5-achievements-20).

---

# 6. Priority 4 — Save Safety with an integrated Ludusavi engine

**Goal:** protect game saves as a native GameHours capability **without requiring the user to install or manage Ludusavi separately** and without rewriting years of save-discovery logic from zero.

## Direction

The preferred architecture is **source-level reuse of Ludusavi's MIT core behind a small GameHours-owned boundary**.

Ludusavi's current Rust package exposes library modules and separates the GUI/CLI `app` feature from the core, but its own `lib.rs` explicitly warns that the library API is unstable. GameHours should therefore contain that instability inside a small native component rather than expose Ludusavi types throughout the .NET application.

Preferred shape:

```text
GameHours (.NET/WPF)
       |
       | versioned JSON contract
       v
GameHours.SaveEngine
small Rust helper shipped with GameHours
       |
       | pinned Ludusavi source/library revision
       v
Ludusavi core + manifest data
```

The user installs **one product: GameHours**.

`GameHours.SaveEngine` should be a small bundled helper, not a second user-facing application and not a permanently running daemon. One-shot JSON operations are the preferred starting point because they isolate Rust/API instability and avoid direct FFI/ABI complexity.

## Explicitly not the preferred approach

- do not require a separately installed `ludusavi.exe` as the normal user path;
- do not port Ludusavi's Rust save engine to C#;
- do not vendor its entire GUI/CLI application;
- do not spread unstable Ludusavi Rust types through .NET via a broad FFI surface;
- do not create a GameHours-specific save manifest from scratch when upstream data can be reused safely.

## Responsibility split

Ludusavi-derived engine owns save-layout/scanning/backup/restore mechanics.

GameHours owns:

- game identity mapping;
- when to back up;
- UI/consent;
- operation scheduling/cancellation;
- backup health/history presentation;
- automatic-after-session policy;
- safety around restore;
- keeping backup failures isolated from playtime/achievement state.

Automatic backup should attach to the existing measured `SessionCompleted` lifecycle. No second process scanner is allowed.

## Delivery order

1. **Save Safety 1 — engine feasibility/bridge:** smallest Rust helper, pinned Ludusavi revision, protocol + read-only preview, packaging/licensing proof.
2. **Save Safety 2 — manual preview/backup:** game mapping, detected-save preview, `Crear copia ahora`, clear unsupported/ambiguous/error states.
3. **Save Safety 3 — automatic after session:** opt-in backup on measured `SessionCompleted`, background/coalesced per game.
4. **Save Safety 4 — history/retention UX.**
5. **Save Safety 5 — restore:** preview, safety backup, explicit confirmation and conflict/downgrade handling.

When Ludusavi code/data is actually distributed, pin the upstream revision and add the required MIT copyright/license to `THIRD-PARTY-NOTICES.md` and packaging verification.

**Primary references:** `mtkennerly/ludusavi`, `mtkennerly/ludusavi-manifest`, and the lifecycle ideas in `mtkennerly/ludusavi-playnite`.

See [`ROADMAP-DETAILS.md#6-save-safety--integrated-ludusavi-engine`](ROADMAP-DETAILS.md#6-save-safety--integrated-ludusavi-engine).

---

# 7. Priority 5 — Platform/source expansion

**Goal:** recognize more real PC libraries while keeping tracking independent from launchers.

Treat three capabilities separately:

1. installed-game/library discovery;
2. playtime/process tracking;
3. achievements.

Proposed research order:

1. **Xbox / Microsoft Store / Game Pass**;
2. **Ubisoft Connect**;
3. **EA Desktop**;
4. **Amazon Games / Battle.net** where reliable local evidence exists;
5. selected console emulators only when a real user case justifies them.

Rules:

- characterize real Windows layouts before implementation;
- prefer official/local evidence first;
- one platform/source per PR where practical;
- no credential scraping;
- no claim of support without real-layout evidence;
- feed existing discovery/resolver/tracker layers instead of creating platform-specific trackers.

See [`ROADMAP-DETAILS.md#7-platformsource-expansion`](ROADMAP-DETAILS.md#7-platformsource-expansion).

---

# 8. Priority 6 — Insights 2.0

**Goal:** exploit information GameHours already owns better than generic launcher counters do.

Candidate insights:

- sessions/day/week/month;
- average and median session duration;
- longest sessions;
- day-of-week/time-of-day habits;
- calendar heatmap;
- executed vs focused vs estimated-active time;
- coverage-aware focus/active ratios;
- recent-vs-lifetime trends;
- achievement activity;
- per-game drill-down.

Rules:

- measured and historical/estimated data remain visually distinguishable;
- never manufacture daily precision for SRUM;
- ratios only use intervals where the required telemetry coverage exists;
- aggregate in SQLite/bulk read models as needed rather than repeatedly materializing large histories in WPF.

**Reference:** ActivityWatch's collection/storage/query separation as a conceptual pattern only.

See [`ROADMAP-DETAILS.md#8-insights-20`](ROADMAP-DETAILS.md#8-insights-20).

---

# 9. Priority 7 — First-run, help and beta UX

**Goal:** make GameHours understandable without requiring the user to understand its architecture.

Before public beta:

- concise first-run onboarding;
- show detected local stores/sources;
- explain optional network metadata separately from local tracking;
- contextual empty/error states;
- direct links to relevant Game Health/Pendientes actions;
- concise explanations for measured vs historical/estimated time;
- diagnostic bundle;
- installation/update/recovery help;
- keyboard/focus/accessibility review;
- localization architecture before the user-facing string surface grows substantially.

Do not create a long mandatory wizard. Defaults should work with only a few decisions.

See [`ROADMAP-DETAILS.md#9-first-run-help-and-beta-ux`](ROADMAP-DETAILS.md#9-first-run-help-and-beta-ux).

---

# 10. Priority 8 — Distribution and trust

Most supply-chain groundwork already exists. The forward work is operational:

- provision Azure Artifact Signing/OIDC;
- produce a real signed release from `main`;
- validate Authenticode for every shipped executable, including future native helpers;
- verify clean install and signed update from a previous release;
- verify recovery/rollback;
- verify package contents/licenses;
- evaluate SmartScreen with the real signed installer;
- publish clear installation/update/uninstall/data-location documentation.

Locked restore, Dependabot, CodeQL and SHA-pinned Actions are baseline unless a fresh audit proves a gap.

See [`ROADMAP-DETAILS.md#10-distribution-and-trust`](ROADMAP-DETAILS.md#10-distribution-and-trust).

---

# 11. Cross-cutting performance track

Performance is evidence-driven, not an excuse for speculative refactors.

Measure when relevant:

- startup/time-to-interactive;
- UI responsiveness;
- CPU idle/tray/game-active;
- Private Memory / Working Set;
- managed allocations/GC when investigating memory;
- SQLite query cost/count;
- artwork/cache behavior;
- repeated disk/network work;
- future SaveEngine startup/operation overhead.

Prefer eliminating work, batching queries, lazy loading, virtualization and bounded caches before low-level tuning. Do not add forced GC or working-set trimming as cosmetic optimization.

See [`ROADMAP-DETAILS.md#11-cross-cutting-performance-track`](ROADMAP-DETAILS.md#11-cross-cutting-performance-track).

---

# 12. Later / deliberately deferred

Reconsider only after the above contracts have matured:

- public provider/plugin SDK;
- local automation/query API;
- optional cross-device/cloud sync built on the existing neutral boundary;
- richer theme customization;
- screenshot souvenirs after a safe capture prototype.

Still excluded unless product direction changes explicitly:

- social/friends/profiles;
- game store/storefront features;
- game installation/uninstallation management.

---

# 13. Recommended next PR sequence

Keep the post-foundation workflow small and independently mergeable:

1. **Library 2.0A — preferences + lightweight search/filtering**;
2. **Game Health 1 — read-only health model/panel**;
3. **Diagnostics 1 — privacy-minimal support bundle**;
4. **Achievements 2.0A — modern Windows notification transport**;
5. **Save Safety 1 — integrated `GameHours.SaveEngine` feasibility/bridge using pinned Ludusavi core**;
6. metadata/save automation/new platforms only after their first slices prove the underlying contracts.

The roadmap is not a queue that must be executed mechanically. Re-evaluate priority when real use reveals a higher-value reliability/product problem.
