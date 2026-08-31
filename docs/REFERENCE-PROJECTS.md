# External engineering references

GameHours researches mature open-source projects before significant product or architecture work. This file records the references that are currently useful **and the license boundary for using them**.

Reference does not automatically mean dependency and does not automatically mean source copying. The default rule is:

1. understand the behavior/problem the external project solves;
2. check whether GameHours already owns the required primitive;
3. prefer an independent implementation when that keeps the design simpler;
4. reuse/adapt source when a permissive license and maintenance advantage make that the better engineering choice;
5. isolate unstable third-party APIs behind GameHours-owned contracts;
6. when source/data is copied, linked or substantially adapted into a distributed build, preserve attribution/license notices required by the upstream project.

GameHours itself is MIT licensed.

---

## License policy

### Permissive/MIT references

Source from MIT projects may be reused or adapted when there is a concrete maintenance advantage. If a substantial portion is incorporated or redistributed:

- keep an attribution comment close to adapted code when practical;
- add the upstream copyright/license text to `THIRD-PARTY-NOTICES.md` before distribution;
- preserve any upstream license file required with redistributed source/data/binaries;
- record the upstream repository, source revision and relevant source files in the implementing PR.

Current MIT references:

- Playnite — `JosefNemec/Playnite`;
- Ludusavi — `mtkennerly/ludusavi`;
- Ludusavi Playnite integration — `mtkennerly/ludusavi-playnite`;
- Ludusavi Manifest — `mtkennerly/ludusavi-manifest`;
- SuccessStory — `Lacro59/playnite-successstory-plugin`.

### Copyleft references

ActivityWatch is MPL-2.0. Achievement Watcher / Achievement Watcher Next are LGPL-3.0. Other useful launcher/emulator projects may be GPL-family licensed.

For these projects GameHours should normally:

- use public behavior, documented formats and architectural ideas as research;
- independently implement the GameHours equivalent;
- avoid pasting implementation code into GameHours' MIT files;
- perform a specific license review before any exception.

This is a project engineering policy, not a replacement for the actual upstream license terms.

### Current repository state

At the time this document was updated, **no Ludusavi/Playnite/SuccessStory source had yet been incorporated into the GameHours distributed build**. Therefore a `THIRD-PARTY-NOTICES.md` file is not created merely for research references. Create/update it in the first implementation PR that actually distributes substantial third-party source/data/binaries.

---

# 1. Playnite — library UX, metadata boundaries, search and diagnostics

**Repository:** `JosefNemec/Playnite`  
**License:** MIT  
**Use:** code/architecture reference; selective adaptation is legally possible with attribution.

Playnite is intentionally broader than GameHours. We should learn from its mature library UX without turning GameHours into a launcher or copying its all-purpose game model.

## Useful source files

### `source/PlayniteSDK/Models/Game.cs`

Useful ideas:

- explicit `Favorite` and `Hidden` library state;
- completion status;
- tags/categories;
- last activity separated from accumulated playtime;
- cover/background/icon metadata;
- developer, publisher, release date, genres and platform metadata.

**Do not copy the model wholesale.** Playnite's `Game` object carries launcher/install/runtime/UI concerns because Playnite is a full library manager. GameHours' `TrackedGame` must remain a small tracking identity. Library preferences and optional metadata should be separate persistence/read models.

### `source/Playnite/ViewModels/SearchViewModel.cs`

Useful ideas:

- asynchronous/cancellable search work;
- delayed searching when expensive work would otherwise repeat;
- deterministic result ordering;
- acronym/word matching for titles;
- keeping current results during transitions to avoid visible flashing.

GameHours should begin simpler: normalized case/diacritic-insensitive matching, title prefixes/acronyms and recent-game ordering. Add fuzzy scoring only when real library sizes show that simpler matching is insufficient.

Do not copy Playnite's plugin/search-context machinery: GameHours does not need that complexity for its first library search.

### `source/PlayniteSDK/MetadataProvider.cs`

Useful idea: metadata retrieval is behind a provider boundary instead of being embedded in the library view.

For GameHours, adapt the concept rather than the exact synchronous API. A GameHours provider should support:

- cancellation;
- explicit online/offline capability;
- local cache/provenance;
- partial results;
- failure without breaking the library.

### `source/Playnite/Diagnostic.cs`

This is the strongest candidate for selective MIT adaptation if/when GameHours implements a diagnostic bundle.

Playnite's package builder demonstrates a useful pattern:

- create a temporary diagnostic workspace;
- collect app/build information;
- collect selected configuration and logs;
- collect system/runtime information;
- include an optional user problem description;
- zip the result for support.

GameHours must be stricter before adapting any of it. The GameHours exporter must centrally redact/exclude:

- Windows usernames;
- full local paths unless explicitly safe;
- tokens/secrets;
- raw SRUM/registry data;
- unrelated personal files.

If implementation copies/substantially adapts `Diagnostic.cs`, retain Josef Nemec/Playnite MIT attribution in source/third-party notices.

---

# 2. Ludusavi — integrated Save Safety engine

**Repository:** `mtkennerly/ludusavi`  
**License:** MIT  
**Copyright:** Copyright (c) 2020 Matthew T. Kennerly (mtkennerly)  
**Preferred relationship:** reuse the MIT core inside a small GameHours-owned native boundary; do **not** require a separately installed Ludusavi application for normal use.

Ludusavi is the clearest case where starting from zero would create avoidable maintenance debt. The hard problem is not copying files into a backup directory; it is maintaining reliable knowledge of save locations, path variables, Windows Registry layouts, store identities, scanning and restore behavior across a huge PC catalogue.

## Why source-level reuse is viable

### `Cargo.toml`

Useful facts from the current upstream structure:

- the package is MIT licensed;
- `default = ["app"]`;
- the `app` feature contains GUI/CLI-oriented dependencies such as `iced`, `clap`, dialogs and presentation tooling;
- core dependencies exist outside that `app` feature.

This means a GameHours native component can investigate consuming Ludusavi as a library with the application feature disabled instead of dragging the complete GUI/CLI application into GameHours.

Pin an exact upstream revision/version in the implementation. Do not float on `master`.

### `src/lib.rs`

Upstream explicitly exposes internal modules including:

- `api`;
- `metadata`;
- `path`;
- `report`;
- `resource`;
- `scan`;
- `serialization`;
- `wrap`.

The same file also explicitly warns that the library API is **unstable** and much of the code was not originally designed as a stable library API.

This warning directly shapes the GameHours architecture: do not leak Ludusavi Rust types throughout .NET or build a broad native ABI around them.

### `src/scan.rs`

Large, mature scanning logic. This is exactly the kind of implementation GameHours should avoid rewriting in C# unless a very narrow independently testable part proves necessary.

### `src/path.rs`

Contains substantial path/layout handling. Again, this is a maintenance-heavy area where source reuse is more valuable than a fresh GameHours port.

### `src/resource.rs` and `src/resource/`

Useful for understanding how upstream resources/manifest data are represented and accessed. The Save Safety feasibility slice must characterize what is needed for an offline packaged GameHours build before choosing the final manifest update strategy.

### `src/api.rs` / `src/serialization.rs`

Useful for understanding upstream machine-readable structures. GameHours should **not** expose these structures directly as its .NET contract. They may change with Ludusavi. The GameHours native boundary translates them into a small versioned GameHours protocol.

## Recommended GameHours architecture

```text
GameHours .NET/WPF
       |
       | GameHours-owned versioned JSON
       v
GameHours.SaveEngine
small Rust helper shipped with GameHours
       |
       | pinned Ludusavi library/source dependency
       v
Ludusavi core
```

`GameHours.SaveEngine` is part of GameHours distribution. It is not a second user-facing application and should start as a one-shot helper process rather than a permanent daemon.

The helper-process boundary is preferred initially over direct FFI because it:

- contains upstream API instability;
- isolates native crashes/panics;
- avoids cross-language pointer/object ownership;
- gives a simple testable JSON protocol;
- allows GameHours to replace/update the underlying engine without changing Desktop/Core contracts.

The user should not need to install or configure `ludusavi.exe` separately.

## Do not do by default

- do not port the complete Rust engine to C#;
- do not vendor the whole Ludusavi GUI/CLI application;
- do not make an external Ludusavi install a normal product prerequisite;
- do not expose upstream unstable types directly to WPF/Core;
- do not invent a second GameHours save-location manifest unless evidence proves the upstream format/lifecycle cannot satisfy the product safely.

## Attribution when implemented

Ludusavi's MIT license states that its copyright and permission notice must be included in copies or substantial portions. When GameHours actually distributes Ludusavi-derived code/binaries:

- add the MIT notice to `THIRD-PARTY-NOTICES.md`;
- record exact upstream revision;
- keep an upstream/revision file close to `GameHours.SaveEngine`;
- include license/package verification in CI;
- document any patches maintained by GameHours.

---

# 3. Ludusavi Manifest — save-layout data

**Repository:** `mtkennerly/ludusavi-manifest`  
**License:** MIT  
**Use:** preferred upstream save-layout dataset consumed through the integrated Ludusavi-derived engine.

GameHours should not translate the manifest into its own independent schema merely to "own" the data. That would create an unnecessary fork and update burden.

The Save Safety engine feasibility slice must prove the cleanest combination of:

- upstream-compatible manifest consumption;
- offline usefulness after installing GameHours;
- pinned/reproducible fallback data;
- safe updates;
- exact provenance/version visibility.

If GameHours redistributes a manifest snapshot, include the upstream MIT attribution/notice and record the shipped revision.

---

# 4. Ludusavi Playnite integration — session lifecycle reference

**Repository:** `mtkennerly/ludusavi-playnite`  
**License:** MIT  
**Language:** C#  
**Use:** lifecycle/coordination reference, not the final architecture for the save engine.

### `src/LudusaviPlaynite.cs`

Useful patterns:

- restore is attached to game-start lifecycle;
- backup is attached to game-stop lifecycle;
- after-play backup runs asynchronously rather than blocking the UI;
- optional periodic during-play backups have explicit timer lifecycle;
- a pending-operation guard avoids overlapping operations;
- stable store IDs are preferred when resolving a title;
- ambiguous/not-found states are surfaced rather than silently guessed;
- operation failures are reported separately from playtime state.

GameHours can simplify this because it already owns authoritative measured session lifecycle.

Preferred GameHours flow:

```text
measured SessionCompleted
        |
        v
SaveBackupCoordinator
        |
        v
GameHours.SaveEngine
        |
        v
Ludusavi-derived core
```

Do not copy the Playnite plugin's menu/settings plumbing. If small coordination/result-handling portions are substantially adapted later, preserve its MIT attribution.

---

# 5. Achievement Watcher Next — Game Health, onboarding and source matrix

**Repository:** `Shirowwww/Achievement-Watcher-Next`  
**License:** LGPL-3.0  
**Use:** behavioral/UX/format research; independent implementation by default.

### `docs/game-health.md`

Useful product pattern:

- one top-level state (`Ready`, `Needs attention`, `Not tracking`);
- one plain-language reason;
- individual checks beneath it;
- Simple mode describes outcomes while Advanced mode exposes exact technical values;
- only repairs that genuinely apply to that game are shown;
- write repairs explain what they change, require confirmation and preserve a backup.

Relevant checks that map well to existing GameHours data:

- executable;
- game identity;
- achievement data/source health;
- live tracking;
- progress/last observation;
- notification transport.

GameHours should additionally expose its own differentiators:

- measured-session health;
- SRUM/historical recovery availability and confidence;
- learned mapping/explicit role state;
- Save Safety state.

Do not copy AW Next's repair code or UI implementation. Build the GameHours health model from existing GameHours services so it does not create a second source of truth.

### `README.md` / source matrix

Useful research targets for future platform slices:

- Xbox PC / Microsoft Store;
- Ubisoft Connect;
- EA Desktop;
- console emulators;
- Steam-compatible local achievement layouts.

Treat reported support as a lead to investigate, not proof that the same approach is correct for GameHours. Characterize each format/layout independently on real data before claiming support.

### Notification behavior

AW Next's Windows/in-game notification behavior is a useful UX reference. GameHours should stop earlier: implement a modern Windows notification transport first and keep the existing transport-neutral unlock event. An overlay is deliberately deferred because its rendering/hooking/anti-cheat cost is much higher.

---

# 6. Achievement Watcher (original) — live local achievement behavior

**Repository:** `xan105/Achievement-Watcher`  
**License:** LGPL-3.0

Useful behavior already reflected in GameHours' achievement architecture:

- file-change-driven observation;
- compare against prior state before notifying;
- deduplicate unlock notifications;
- account for formats that flush state at process exit;
- optional automatic screenshot around a new unlock.

GameHours already independently implements the important monitoring semantics around measured sessions and SQLite persistence. Do not replace that with Achievement Watcher source.

The screenshot-souvenir concept remains a later product experiment; research a native Windows capture path from official documentation before looking at third-party implementation details.

---

# 7. SuccessStory — completion, rarity and achievement presentation

**Repository:** `Lacro59/playnite-successstory-plugin`  
**License:** MIT  
**Language:** C#

Useful source files:

### `source/Models/Achievement.cs`

Useful data ideas:

- stable API name separate from display name;
- unlocked/locked artwork;
- UTC-aware unlock date handling;
- hidden state;
- global rarity percentage;
- categories and richer presentation metadata;
- local image-cache awareness.

GameHours already has stronger source/evidence confidence semantics. If rarity is added, it should enrich the existing achievement/read model rather than replacing it.

### `source/Models/AchRaretyStats.cs`

A deliberately small example of aggregating locked/unlocked/total counts. GameHours already has equivalent completion summaries, so there is no reason to copy this class.

### `source/Models/GameStats.cs`

Useful reminder to keep generic statistic presentation separate from provider-specific fields. For GameHours Insights, prefer typed internal aggregates and thin presentation models rather than a catch-all stats object.

---

# 8. ActivityWatch — insights architecture, not tracking scope

**Repository:** `ActivityWatch/activitywatch`  
**License:** MPL-2.0  
**Use:** conceptual architecture/UX research only by default.

Useful ideas:

- collection and presentation/querying are separate concerns;
- time-series events can be aggregated by date/range/category without changing raw collection semantics;
- dashboard and timeline are projections over stored events;
- raw data/export are separate from user-friendly summaries;
- long-term value comes from making collected data explorable, not just collecting more signals.

GameHours should apply this only to game data it already owns: sessions, focused/active telemetry, achievements and historical evidence.

Explicitly do **not** expand GameHours into ActivityWatch-style browser history, general window-title surveillance or editor tracking.

---

# 9. Future platform research sources

The roadmap names Xbox, Ubisoft, EA and later Amazon/Battle.net/emulators as platform/source candidates. Before each platform implementation:

1. prefer official launcher/platform documentation and local on-disk/database evidence;
2. inspect mature open-source adapters for format/location hints;
3. check license before copying any parser;
4. reproduce on a real Windows installation;
5. write a short format/layout note in `docs/` before claiming support.

Useful starting references include:

- Playnite library extensions (platform discovery patterns; licenses vary per extension);
- Heroic Games Launcher for store/provider separation (GPL-family source: research/reimplementation, not casual copying);
- Achievement Watcher Next for achievement-source coverage leads (LGPL-3.0);
- GameHours' existing Steam/Epic/GOG adapters as the preferred internal pattern.

---

# 10. Reference checklist for future PRs

Every PR that uses one of these references should state in its description:

- which upstream project/file was studied;
- whether the implementation is independent, linked as a dependency, vendored or source-adapted;
- upstream license;
- exact upstream revision/version when distributed;
- whether attribution/third-party notice is required;
- why the chosen approach is simpler/better for GameHours than starting from zero or importing a broader dependency.

This keeps external research useful without letting reference code silently turn into untracked licensing or maintenance debt.
