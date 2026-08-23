# Windows distribution pipeline

GameHours uses Velopack for Windows packaging and updates. Distribution is deliberately split into stages so CI can prove package quality without pretending that a production hosting/signing setup already exists.

## Current automated stages

### 1. Normal CI validation

The Windows CI always performs:

```text
restore
  -> build
  -> test
  -> desktop publish smoke
```

Draft pull-request synchronizations stop there deliberately. Once a pull request is ready for review, and on `main` or manual dispatch, CI also runs the Velopack package smoke and release-artifact validation.

The package smoke uses a synthetic SemVer (`0.0.1-ci.<run>`) and the `beta` channel. It does not publish anything externally.

This catches breakage in the actual packaging path before merge without spending packaging resources on every draft synchronization. CI #372 was the first full gate to prove the explicit WPF Velopack entry point, normal test suite, desktop publish and Velopack packaging together. Subsequent ready-for-review/main package gates continue to exercise the same path.

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

## WPF/Velopack entry point

The packaged main binary is `GameHours.Desktop.exe`. Velopack lifecycle handling therefore runs directly from `GameHours.Desktop.App.Main` before WPF initialization.

The WPF project follows Velopack's recommended custom-entry-point structure:

- `App.xaml` is compiled as `Page` rather than `ApplicationDefinition`;
- `GameHours.Desktop.App` is the `StartupObject`;
- `[STAThread] Main` executes `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` first;
- normal `App.InitializeComponent()` / `App.Run()` occurs only afterwards.

This is important for correctness as well as packaging verification: install/update hooks can run and exit without loading the normal WPF application. Auto-apply remains disabled so GameHours still controls when the live tracker is shut down for an update.

## Update source configuration

A package can embed a read-only update origin through `update-source.txt`. The manual packaging workflow reads the optional repository variable:

```text
GAMEHOURS_UPDATE_SOURCE
```

This value is expected to be a normal HTTPS/feed URL and is **not a credential**. If the variable is absent, the package remains valid but in-app self-update is disabled unless `GAMEHOURS_UPDATE_SOURCE` is supplied externally at runtime.

Never place a GitHub PAT, cloud secret or signing credential in `update-source.txt`, repository variables intended for the client, release notes, or package contents.

## Production publishing is intentionally separate

The GameHours repository is public, so anonymous users can read public release assets. That makes GitHub Releases one possible future distribution surface, but the current workflows deliberately do not treat repository visibility as a production deployment decision.

The production update origin is still unselected. It must be a read-only HTTPS location that installed clients can access without credentials and that exposes the Velopack release index/packages required by the installed channel. A public GitHub release/feed may be used if it satisfies that contract and is validated end to end; a static HTTPS origin is also valid.

The desktop application must never embed a GitHub PAT or deployment credential merely to access updates.

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
- [x] package smoke in the ready-for-review/main CI path;
- [x] explicit WPF Velopack main-entry bootstrap verified by packaging CI;
- [x] manually dispatched package workflow producing an installable Actions artifact;
- [ ] real-machine validation of the current packaged WPF Desktop path (tracked in `REAL-MACHINE-VALIDATION.md`);
- [ ] select and validate the production read-only HTTPS update origin;
- [ ] add Velopack remote download/upload to the release workflow once that origin exists;
- [ ] configure Windows code signing;
- [ ] validate a signed stable install/update/rollback path end to end.

See also [`UPDATES.md`](UPDATES.md) for application-side update behavior and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md) for deferred installed-machine checks.
