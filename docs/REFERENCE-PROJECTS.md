# External engineering references

GameHours researches mature open-source projects before significant product or architecture work. This file records the references that are currently useful **and the license boundary for using them**.

Reference does not mean dependency and does not mean source copying. The default rule is:

1. understand the behavior/problem the external project solves;
2. check whether GameHours already has the required primitive;
3. prefer an independent implementation adapted to GameHours;
4. copy/adapt source only when that is clearly simpler or safer than reimplementation;
5. when source is copied or substantially adapted, preserve the attribution and license notice required by that source.

GameHours itself is MIT licensed.

---

## License policy

### Permissive/MIT references

Source from MIT projects may be reused or adapted when there is a concrete maintenance advantage. If a substantial portion is incorporated:

- keep an attribution comment close to the adapted code when practical;
- add the upstream copyright/license text to `THIRD-PARTY-NOTICES.md` before distribution;
- record the upstream repository and source file in the implementing PR.

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

At the time this document was created, **no new third-party source was copied into GameHours as part of the roadmap work**. Therefore a `THIRD-PARTY-NOTICES.md` file is not created merely for research references. Create/update it when a future implementation actually incorporates a substantial licensed portion.

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

- empty search surfaces recent games;
- search is cancellable;
- asynchronous work does not block WPF;
- delayed searching can avoid repeated expensive work;
- ranking combines sensible text matching with deterministic tie-breaks;
- acronym/word matching helps titles without requiring a heavyweight search engine.

GameHours should begin simpler: normalized case/diacritic-insensitive matching, title prefixes/acronyms and recent-game ordering. Add fuzzy scoring only when real library sizes show that simpler matching is insufficient.

Do not copy Playnite's timers/search-context/plugin machinery: GameHours does not need that complexity for its first library search.

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

# 2. Ludusavi — save safety without reinventing save discovery

**Repository:** `mtkennerly/ludusavi`  
**License:** MIT  
**Preferred relationship:** external process/API integration, not source vendoring.

## Useful contracts

### `docs/cli.md`

Ludusavi already exposes the boundary GameHours needs:

- `backup` / `restore`;
- `--preview`;
- machine-readable `--api` output;
- bulk `api` JSON requests;
- `schema` commands for the API/config/output formats;
- `find` with stable Steam/GOG identifiers before title matching;
- full/differential retention;
- cloud/conflict/downgrade controls;
- `wrap` for before/after-game lifecycle use cases.

**Recommended GameHours implementation:** invoke the installed Ludusavi executable, consume only machine-readable output and let Ludusavi own its manifest/config/backup format.

Do not copy its Rust save-scanning implementation and do not initially vendor the Ludusavi Manifest. This keeps GameHours small while benefitting from a separately maintained catalogue.

### `mtkennerly/ludusavi-manifest`

The manifest is MIT and maintained independently. It is a useful fallback/reference if the external CLI boundary ever proves insufficient, but vendoring it would create update/versioning responsibilities in GameHours. Prefer not to do so initially.

---

# 3. Ludusavi Playnite integration — session lifecycle pattern

**Repository:** `mtkennerly/ludusavi-playnite`  
**License:** MIT  
**Language:** C#

### `src/LudusaviPlaynite.cs`

This is the most directly relevant implementation reference for a future GameHours adapter.

Useful patterns:

- restore is attached to the game-start lifecycle;
- backup is attached to the game-stop lifecycle;
- after-play backup runs asynchronously rather than blocking the UI;
- optional periodic during-play backups have explicit timer lifecycle;
- a pending-operation guard avoids overlapping operations;
- stable store IDs are preferred when resolving a Ludusavi title;
- ambiguous/not-found states are surfaced rather than silently guessed;
- operation failures are reported separately from the launcher's own playtime state.

GameHours can simplify this because it already owns authoritative `SessionStarted`/`SessionCompleted` events. The first integration should therefore use:

`measured SessionCompleted -> optional save-backup coordinator -> Ludusavi API`

and should not create another process watcher.

Do not copy the Playnite plugin's menu/settings plumbing. If the process invocation/result handling is substantially adapted later, preserve its MIT attribution.

---

# 4. Achievement Watcher Next — Game Health, onboarding and source matrix

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
- optional save-backup provider state.

Do not copy AW Next's repair code or UI implementation. The GameHours health model should be built from existing GameHours services so it does not create a second source of truth.

### `README.md` / source matrix

Useful research targets for future platform slices:

- Xbox PC / Microsoft Store;
- Ubisoft Connect;
- EA Desktop;
- console emulators;
- Steam-compatible local achievement layouts.

Treat reported support as a lead to investigate, not proof that the same approach is correct for GameHours. Characterize each format/layout independently on real data before claiming support.

### Notification behavior

AW Next's automatic choice between in-game popup and Windows notification is a useful UX reference. GameHours should stop earlier: implement a modern Windows notification transport first and keep the existing transport-neutral unlock event. An overlay is deliberately deferred because its rendering/hooking/anti-cheat cost is much higher.

---

# 5. Achievement Watcher (original) — live local achievement behavior

**Repository:** `xan105/Achievement-Watcher`  
**License:** LGPL-3.0

Useful behavior already reflected in GameHours' achievement architecture:

- file-change-driven observation;
- compare against prior state before notifying;
- deduplicate unlock notifications;
- account for formats that flush state at process exit;
- optional automatic screenshot around a new unlock.

GameHours already independently implements the important monitoring semantics around its measured sessions and SQLite persistence. Do not replace that with Achievement Watcher source.

The screenshot-souvenir concept remains a later product experiment; research a native Windows capture path from official documentation before looking at third-party implementation details.

---

# 6. SuccessStory — completion, rarity and achievement presentation

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

GameHours already has stronger source/evidence confidence semantics. If rarity is added, it should enrich the existing achievement model/read model rather than replacing it.

### `source/Models/AchRaretyStats.cs`

A deliberately small example of aggregating locked/unlocked/total counts. GameHours already has equivalent completion summaries, so there is no reason to copy this class.

### `source/Models/GameStats.cs`

Useful reminder to keep generic statistic name/value presentation separate from provider-specific fields. For GameHours Insights, prefer typed internal aggregates and thin presentation models rather than a catch-all stats object.

---

# 7. ActivityWatch — insights architecture, not tracking scope

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

# 8. Future platform research sources

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

# 9. Reference checklist for future PRs

Every PR that uses one of these references should state in its description:

- which upstream project/file was studied;
- whether the implementation is independent or source-adapted;
- upstream license;
- whether attribution/third-party notice is required;
- why the chosen approach is simpler/better for GameHours than starting from zero or importing a dependency.

This keeps external research useful without letting reference code silently turn into untracked licensing or maintenance debt.
