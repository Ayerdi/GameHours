# GameHours SaveEngine upstream pins

- Ludusavi repository: https://github.com/mtkennerly/ludusavi
- Ludusavi version: `0.31.0`
- Ludusavi commit: `8844d7b67e784909f4ef42f7bfb047b700fe7b15`
- Ludusavi tag object: `0edde60fdb26a1ff897db6789188ede44f4ed050`
- `ludusavi-manifest` compatibility baseline: `0901bc3f86b91fad0c30e7550fd1cfb09474f307`
- Reviewed: 2026-09-12
- Local integration patches to Ludusavi: none

The first Save Safety slice links Ludusavi as a Rust library with `default-features = false` behind a GameHours-owned one-shot JSON process boundary. The full upstream GUI/CLI is not distributed.

The primary `ludusavi-manifest` dataset is not yet vendored into GameHours in this slice. Contract tests use a small GameHours-authored fixture that follows the public manifest schema. The compatibility revision above records what was reviewed for the bridge and is not a claim that that dataset is bundled.

## Transitive license review

The Windows runtime graph is audited from `Cargo.lock` by `scripts/generate-save-engine-notices.ps1`. The first feasibility build resolves one MPL-2.0-only crate, `option-ext 0.2.0`, through `Ludusavi -> dirs -> dirs-sys -> option-ext`. It is consumed unchanged. Mozilla's MPL 2.0 FAQ explicitly permits an MPL component to be statically linked into a larger work while keeping the larger work under separate terms, provided recipients are informed how to obtain the MPL-covered source. GameHours therefore ships the MPL text and source locations in its third-party notices.

No GPL/LGPL/AGPL-only dependency was found in the Windows runtime graph. Where a crate offers multiple licenses, GameHours relies on a permissive option (for example Apache-2.0 instead of the GPL alternative offered by `self_cell`).
