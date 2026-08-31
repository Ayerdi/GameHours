# Achievement evidence architecture

## Purpose

GameHours can usually read achievement state directly from Steam-compatible local state (Steam, RUNE, GSE/Goldberg and other supported formats). Some installations, however, retain only future unlocks and lose historical state. A save file can sometimes prove that a historical achievement condition was completed, but it normally cannot prove the full locked/unlocked catalogue.

This subsystem exists to recover **positive, auditable evidence** without pretending that a save is an authoritative achievement database.

## Design principles

1. **Positive-only evidence.** A provider may emit `ConfirmedAchievementUnlockEvidence` only when it can prove an unlock. Missing evidence means unknown, never locked.
2. **Platform state stays separate.** Save-derived evidence is not disguised as RUNE/Steam/GSE state. Provenance and rule information remain available for diagnostics and future correction.
3. **Game-specific adapters, shared engine.** File formats and achievement rules belong to individual providers. Provider execution, diagnostics, deduplication, persistence and presentation belong to reusable GameHours infrastructure.
4. **Read-only by construction.** Evidence providers do not edit saves, invoke achievement-unlock APIs or mutate emulator/platform state.
5. **Stable identity before title matching.** Providers should prefer platform IDs and executable/install identity. Display titles are not sufficient on their own for automatic recovery.
6. **Rules are versioned, explainable and explicitly active.** Every proof records provider, rule ID, rule version, source fingerprint and a human-readable explanation. Historical rule revisions remain auditable, but only revisions declared active by the current application may affect the effective unlock projection.
7. **Failures are isolated.** A corrupt or unsupported save for one provider must not discard positive evidence from another provider.
8. **No dynamic plugin loader yet.** Providers are internal typed implementations registered by GameHours. An external plugin system would add compatibility and security surface without current product value.

## Current foundation

The reusable contract lives in `GameHours.Windows.Achievements.Evidence`:

- `AchievementEvidenceRequest` — game identity and observation context.
- `IAchievementUnlockEvidenceProvider` — one game/provider extension point.
- `AchievementEvidenceReadResult` — `Success`, `NotApplicable`, `NoEvidence` or `Failed`.
- `AchievementEvidenceProviderChain` — runs all providers and combines independent positive proofs.
- `ISaveStateParser<TState>` — format/state parsing contract, independent from provider execution.
- `SaveFileAchievementEvidenceProvider<TState>` — reusable read-only save runner that owns discovery, metadata fingerprints, caching, concurrency, parse sharing and per-file failure isolation.
- `IAchievementEvidenceRule<TState>` — deterministic positive-only rule evaluated against an already parsed state.

The neutral proof model lives in `GameHours.Core.Domain` as `ConfirmedAchievementUnlockEvidence`. Active rule revisions are represented by `AchievementEvidenceRuleIdentity`, so validity is a projection concern rather than mutable state on historical evidence rows.

The provider chain deliberately runs every registered provider instead of stopping at the first success: two independent sources can prove the same achievement and both proofs are useful for auditability. `ConfirmedApiNames` collapses those proofs only when a consumer needs the set of confirmed unlock IDs.

The generic save infrastructure is deliberately not a universal binary parser. Different formats may require different `ISaveStateParser<TState>` implementations; games that share a format can reuse the same parser while keeping their applicability and achievement rules separate.

## Relationship to local achievement state

The existing local achievement reader answers:

> What does the platform/emulator state currently tell us?

The evidence subsystem answers:

> What additional unlocks can GameHours independently prove?

These answers are reconciled monotonically:

```text
platform/emulator positive unlocks
              +
active confirmed achievement evidence
              |
              v
     confirmed unlocked set
```

A partial source must never turn the complement of that set into `locked`.

## Provider requirements

A provider is accepted for automatic recovery only when all of the following are true:

- applicability can be established reliably for the game/install;
- parsing is deterministic and read-only;
- each automatic rule proves the achievement condition rather than merely correlating with it;
- the rule has regression tests with representative positive and negative states;
- loading an advanced save does not manufacture evidence for conditions that are not actually persisted;
- source changes or unsupported save versions fail closed (`NoEvidence`/`Failed`) rather than guessing.

Rules that are plausible but not conclusive may be useful for diagnostics, but they must not emit `ConfirmedAchievementUnlockEvidence`.

## Performance model

Save parsing must never run on the UI thread. Providers should compute a cheap metadata fingerprint first (normalized path, size and `LastWriteTimeUtc`) and use an in-memory cache before opening or decompressing unchanged data. Content hashing is reserved for formats whose metadata is not reliable.

Persisted evidence retains the fingerprint that produced each positive proof for auditability, but it is deliberately not exposed as a "source processed" cache. A false rule leaves no evidence, and a new rule version must still be evaluated, so the presence of one positive row cannot prove that the current rule set has fully processed a source. The in-memory provider cache should remember both positive and no-evidence scans; after an application restart, reparsing once is preferable to persisting an artificial negative assertion or a second cache table.

Multiple save slots may contribute positive evidence. Once an unlock has been reliably proven by an older slot, a newer slot not containing the same condition does not negate it.

## Persistence and rule validity

Schema v7 persists evidence in its own `achievement_unlock_evidence` table rather than losing provenance inside `achievement_states`. The durable identity includes:

- game ID;
- achievement API name;
- provider;
- rule ID and version;
- source path/fingerprint;
- first/last observation time;
- explanation.

Repeated observations upsert the same proof, preserve its first observation and refresh its last observation/fingerprint/detail. A new rule version coexists with the old one for auditability.

Persisted evidence is intentionally append/audit oriented rather than carrying an `IsRevoked` flag. Before supplemental evidence contributes to an unlock projection, `AchievementEvidenceRulePolicy` keeps only rows whose `(provider, achievement API name, rule ID, rule version)` match a rule identity declared active by the current application. If v1 produced a false positive and v2 replaces it, v1 remains inspectable in storage but stops affecting the product. A removed rule likewise contributes nothing. This avoids a schema migration and makes the current rule registry the single source of truth for validity.

The existing monotonic achievement state remains the projection used by normal UI/activity features, while each recovered unlock remains traceable to its evidence record.

Portable JSON v2 includes the domain proof (game/API, origin, provider, rule/version, detail and observation bounds) but excludes source path and fingerprint because those values are machine-specific. V1 imports remain supported and simply contain no supplemental evidence.

## Presentation

`AchievementPresentation` owns the shared count semantics used by both Library and game detail so those surfaces cannot disagree about certainty.

When the catalogue is complete but the historical state is partial, the UI communicates a lower bound rather than a false exact count:

- complete state, 10 of 28 → `10/28`;
- complete state, 0 of 42 → `0/42`;
- incomplete/positive-only state, 10 confirmed of 28 → `10+/28`;
- incomplete/positive-only state, no confirmed historical unlocks of 42 → `?/42`;
- incomplete catalogue, four confirmed unlocks → `4 confirmados`.

Exact completion percentages are shown only when the state coverage is complete. The `+` means “at least this many confirmed”; it disappears once an authoritative complete state becomes available.

## First adapter: Gothic 1 Remake

Gothic 1 Remake is the first validation adapter, not a special case in the engine. Its provider owns Gothic-specific save discovery and state projection while relying on the reusable save-evidence infrastructure for execution, caching, concurrency and diagnostics. Adding a second supported game should require a parser/profile/rules appropriate to its format, not modifications to the provider chain, persistence model or UI semantics.

Only rules classified as unequivocally provable from persisted state will be enabled automatically. Historical/event-only achievements whose conditions are not preserved remain unknown.

## External references informing the design

- Playnite exposes narrow, typed extension interfaces rather than baking game-specific integrations into its core. GameHours follows the same separation principle, without taking on an external plugin loader yet.
- RetroAchievements' developer guidance emphasizes that current state alone is often insufficient and that save-related achievement logic needs explicit protection, multiple conditions where necessary and regression testing. GameHours applies the same conservative standard to save-derived recovery evidence.
