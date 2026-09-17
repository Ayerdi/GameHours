# Ludusavi Manifest upstream

- Repository: https://github.com/mtkennerly/ludusavi-manifest
- Revision: `911eafe249166f6e115ab53a73f6c3532ed6e7b5`
- Source file: `data/manifest.yaml`
- Upstream SHA-256: `193A7B47A9DD80E9CA1C239D7CF78B50720902D4B0F9BC38D23949715E4B77BF`
- Distributed sanitized SHA-256: `87E69A3CE52F1170FF35FEDA47D0041E5BE21E4195478722684C8E119469896D`
- License: MIT

GameHours vendors only the primary manifest data needed for offline save-location discovery. `scripts/generate-save-manifest.ps1` deterministically strips all top-level-per-game `launch` blocks, which are not used by Ludusavi 0.31 save scanning and can contain historical launcher credentials. The remaining save-layout data stays in upstream YAML form and is loaded by the pinned Ludusavi core.

Updating this file is a deliberate dependency update: pin the new upstream revision/hash, update the generator's expected hashes, regenerate the sanitized snapshot, refresh third-party notices, then rerun the SaveEngine contract and package-smoke tests.
