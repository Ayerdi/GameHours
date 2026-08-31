# Supply-chain hardening

GameHours keeps supply-chain controls deliberately small and reviewable: reproducible NuGet restores, automated dependency updates, immutable GitHub Action references, least-privilege workflow tokens and verifiable release provenance.

## Reproducible NuGet graph

`Directory.Build.props` enables package lock files and every solution project commits its `packages.lock.json`. CI and packaging restore with `--locked-mode`, so a dependency graph that no longer matches the repository fails instead of silently resolving different packages.

`GameHours.Desktop` declares its supported runtime as `win-x64`. The packaging script therefore uses:

```text
dotnet restore <Desktop.csproj> --locked-mode
```

followed by:

```text
dotnet publish <Desktop.csproj> -r win-x64 --self-contained true --no-restore
```

The publish step cannot perform a hidden second dependency resolution.

## Dependency maintenance

`.github/dependabot.yml` proposes weekly NuGet and GitHub Actions updates independently, with bounded open-PR counts so changes remain reviewable. Lock-file diffs expose transitive dependency changes.

All external Actions used by GameHours are pinned to full immutable commit SHAs. Human-readable version comments are informational only.

## Workflow permissions

Normal CI keeps `contents: read` and checkout uses `persist-credentials: false`.

The release workflow has the additional narrowly-scoped permissions required for its release-only controls:

```yaml
id-token: write
attestations: write
```

`id-token: write` is used for short-lived OIDC authentication to Azure, not for a stored Azure password. `attestations: write` lets GitHub record provenance for the checksummed release artifacts.

## Windows release signing

Public Windows releases target Azure Artifact Signing. GameHours does not store a PFX, private key or certificate password in the repository or artifacts.

Velopack receives a temporary metadata file containing only:

- Artifact Signing HTTPS endpoint;
- signing account name;
- certificate profile name.

GitHub authenticates to Azure through a federated identity. The Azure identity should receive only the certificate-profile signing role needed by this workflow.

When signing is enabled, GameHours does not trust a successful `vpk pack` alone: the release validator verifies Authenticode on the newest Setup and on the GameHours executables inside the newest full package before checksums/attestation/upload.

## Release provenance

The manual release workflow generates `SHA256SUMS.txt` after package/signature validation and passes that manifest to `actions/attest`. Authenticode and artifact attestation serve different purposes:

- **Authenticode**: Windows publisher identity and signed-file integrity;
- **GitHub artifact attestation**: provenance tying artifact hashes to a repository commit/workflow execution.

Neither control is described as a substitute for the other.

## Automated evidence

The supply-chain and packaging path has progressed through multiple clean Windows CI runs. In particular:

- CI #690 validated the real `scripts/package-windows.ps1` Velopack packaging path after the RID/locked-restore fix;
- CI #699 validated two consecutive Velopack versions in one feed and required a generated delta package;
- CI #700 passed locked restore, Release build with 0 warnings/0 errors, 130 Core tests + 139 Windows tests = 269/269, and win-x64 self-contained publish on the attestation-workflow HEAD.

Azure OIDC, Artifact Signing and the GitHub attestation step remain **configuration/runtime validation pending** until the release workflow is executed from `main` with the external Azure resources and repository settings configured. They are not falsely marked verified from normal CI.

## GitHub-hosted security settings

The repository protects `main` with pull-request, status-check, review-thread, squash-only, non-fast-forward and deletion rules. Repository-hosted security features should prefer GitHub's low-maintenance defaults unless a concrete need requires custom workflow code.

Before calling the hosted-security portion fully complete, verify in GitHub settings:

1. CodeQL Default setup;
2. Dependabot alerts/security updates;
3. secret scanning + push protection;
4. policy requiring Actions pinned to full-length commit SHAs.

Repository files alone do not prove those settings are enabled.

## Change discipline

When a dependency/action update is proposed:

- review release/security context;
- inspect the lock-file/transitive graph diff;
- require normal Windows CI;
- avoid unrelated lock regeneration;
- keep Action references at immutable SHAs.

Before a public Windows release:

- require a read-only HTTPS update origin;
- require Azure signing configuration and valid Authenticode output;
- require SHA-256 manifest + GitHub artifact attestation;
- then perform the real installed update/recovery and SmartScreen checks.
