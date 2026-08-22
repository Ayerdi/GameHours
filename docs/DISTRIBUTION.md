# Windows distribution pipeline

GameHours uses Velopack for Windows packaging and updates. Distribution is deliberately split into stages so CI can prove package quality without pretending that a production hosting/signing setup already exists.

## Current automated stages

### 1. Normal CI package smoke

The main Windows CI now performs:

```text
restore
  -> build
  -> test
  -> desktop publish smoke
  -> Velopack package smoke
  -> release artifact validation
```

The package smoke uses a synthetic SemVer (`0.0.0-ci.<run>`) and the `beta` channel. It does not publish anything externally.

This catches breakage in the actual packaging path on every PR instead of discovering it only when a release is needed.

### 2. Manual Package Windows workflow

`.github/workflows/package-windows.yml` is a manually dispatched workflow with two explicit inputs:

- release `version` (SemVer);
- `beta` or `stable` channel.

It restores, builds and tests the solution, invokes the same `scripts/package-windows.ps1` used locally, validates the produced release, then uploads the complete Velopack release directory as a GitHub Actions artifact for 14 days.

This workflow **builds an installable candidate; it does not publish a production release**.

### 3. Release validation

`scripts/validate-velopack-release.ps1` verifies that packaging produced at least:

- `releases.<channel>.json` containing valid JSON;
- a full `.nupkg`;
- a Velopack Setup executable;
- no zero-length output files.

It also writes `SHA256SUMS.txt` for every produced release artifact.

`scripts/package-windows.ps1` always invokes this validator before reporting success, so local and CI packages share one quality gate.

## Update source configuration

A package can embed a read-only update origin through `update-source.txt`. The manual packaging workflow reads the optional repository variable:

```text
GAMEHOURS_UPDATE_SOURCE
```

This value is expected to be a normal HTTPS/feed URL and is **not a credential**. If the variable is absent, the package remains valid but in-app self-update is disabled unless `GAMEHOURS_UPDATE_SOURCE` is supplied externally at runtime.

Never place a GitHub PAT, cloud secret or signing credential in `update-source.txt`, repository variables intended for the client, release notes, or package contents.

## Why the workflow does not publish to private GitHub Releases

The GameHours repository is currently private. A normal installed desktop application cannot anonymously consume release assets from a private GitHub repository; doing so would require shipping or acquiring GitHub credentials in the client.

GameHours therefore does not use a private GitHub Release as its production update origin and does not embed a PAT in the desktop application.

The production update origin should instead be a read-only HTTPS location that installed clients can access without secrets and that exposes the Velopack release index/packages required by the installed channel.

## Delta updates and production publishing

Velopack's recommended release lifecycle is conceptually:

```text
download existing remote releases
            |
            v
        pack new release
            |
            v
        upload release/feed
```

Downloading the previous remote release metadata/packages before packing allows Velopack to create useful delta packages in a stateless CI runner. The current manual workflow intentionally stops before this stage because GameHours does not yet have a selected production update host.

Once the host is selected, extend the workflow with the matching Velopack `download` and `upload` commands rather than manually copying individual feed files. The remote host, retention policy and credentials belong to the deployment workflow, not the desktop client.

## Channels

GameHours currently ships only Windows x64, so `beta` and `stable` are sufficient channel names. If another OS/architecture is introduced, channel names must be revisited before public distribution so incompatible artifacts can never share one feed.

Do not hard-code a different channel in the update client. The installed Velopack package carries its own channel identity.

## Code signing

The current packages are unsigned development/release candidates. Public Windows distribution is not complete until the executable/updater/installer signing path is configured and validated.

Signing credentials must be supplied only at release time through an appropriate protected mechanism (for example a signing service or protected CI secret/OIDC integration). They must never be committed to the repository or copied into artifacts as raw secrets.

## Remaining distribution work

- [x] reproducible local packaging command;
- [x] package output validation;
- [x] SHA-256 manifest generation;
- [x] package smoke in normal CI;
- [x] manually dispatched package workflow producing an installable Actions artifact;
- [ ] real-machine validation of the current packaged WPF Desktop path (tracked in `REAL-MACHINE-VALIDATION.md`);
- [ ] select the production read-only HTTPS update origin;
- [ ] add Velopack remote download/upload to the release workflow once that origin exists;
- [ ] configure Windows code signing;
- [ ] validate a signed stable install/update end to end.

See also [`UPDATES.md`](UPDATES.md) for application-side update behavior and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md) for deferred installed-machine checks.
