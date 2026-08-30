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
6. **Rules are versioned and explainable.** Every proof records provider, rule ID, rule version, source fingerprint and a human-readable explanation.
7. **Failures are isolated.** A corrupt or unsupported save for one provider must not discard positive evidence from another provider.
8. **No dynamic plugin loader yet.** Providers are internal typed implementations registered by GameHours. An external plugin system would add compatibility and security surface without current product value.

## Current foundation

The reusable contract lives in `GameHours.Windows.Achievements.Evidence`:

- `AchievementEvidenceRequest` — game identity and observation context.
- `IAchievementUnlockEvidenceProvider` — one game/provider extension point.
- `AchievementEvidenceReadResult` — `Success`, `NotApplicable`, `NoEvidence` or `Failed`.
- `AchievementEvidenceProviderChain` — runs all providers and combines independent positive proofs.

The neutral proof model lives in `GameHours.Core.Domain` as `ConfirmedAchievementUnlockEvidence`.

The provider chain deliberately runs every registered provider instead of stopping at the first success: two independent sources can prove the same achievement and both proofs are useful for auditability. `ConfirmedApiNames` collapses those proofs only when a consumer needs the set of confirmed unlock IDs.

## Relationship to local achievement state

The existing local achievement reader answers:

> What does the platform/emulator state currently tell us?

The evidence subsystem answers:

> What additional unlocks can GameHours independently prove?

These answers are reconciled monotonically:

```text
platform/emulator positive unlocks
              +
confirmed achievement evidence
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

Save parsing must never run on the UI thread. The persistence phase will record a lightweight source fingerprint so unchanged files are not reparsed on every refresh. A provider should perform its cheap applicability checks before opening or decompressing save data.

Multiple save slots may contribute positive evidence. Once an unlock has been reliably proven by an older slot, a newer slot not containing the same condition does not negate it.

## Persistence phase

The next infrastructure slice will persist evidence in its own table rather than losing provenance inside `achievement_states`. The durable identity will include at least:

- game ID;
- achievement API name;
- provider;
- rule ID and version;
- source path/fingerprint;
- first/last observation time;
- explanation.

The existing monotonic achievement state remains the projection used by normal UI/activity features, but it must be possible to trace a recovered unlock back to its evidence record.

## Presentation

When the catalogue is complete but the historical state is partial, the UI must communicate a lower bound rather than a false exact count. For example, `9+/42` means “at least nine confirmed; historical state is incomplete”. Once a complete authoritative state becomes available, the `+` disappears.

## First adapter: Gothic 1 Remake

Gothic 1 Remake is the first validation adapter, not a special case in the engine. Its provider will own Gothic-specific save discovery, decompression/reading and achievement rules. Adding a second supported game should require a new provider and tests, not modifications to the provider chain, persistence model or UI semantics.

Only rules classified as unequivocally provable from persisted state will be enabled automatically. Historical/event-only achievements whose conditions are not preserved remain unknown.

## External references informing the design

- Playnite exposes narrow, typed extension interfaces rather than baking game-specific integrations into its core. GameHours follows the same separation principle, without taking on an external plugin loader yet.
- RetroAchievements' developer guidance emphasizes that current state alone is often insufficient and that save-related achievement logic needs explicit protection, multiple conditions where necessary and regression testing. GameHours applies the same conservative standard to save-derived recovery evidence.
