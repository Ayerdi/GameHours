# Windows distribution pipeline

GameHours uses Velopack for Windows packaging and updates. Distribution is deliberately split into stages so CI can prove package quality without pretending that production hosting and publisher identity already exist.

## Current automated stages

### 1. Normal CI validation

The Windows CI always performs:

```text
restore
  -> build
  -> test
  -> desktop publish smoke
```

Draft pull-request synchronizations stop there deliberately. Once a pull request is ready for review, and on `main` or manual dispatch, CI also exercises the Velopack packaging path. The update-chain smoke builds two consecutive versions in the same feed and requires a delta package, so CI verifies the packaging contract used by an incremental update without publishing externally.

### 2. Manual Package Windows workflow

`.github/workflows/package-windows.yml` is the release-candidate workflow. It accepts an explicit SemVer and `beta`/`stable` channel, but deliberately fails unless dispatched from `main` with all production prerequisites configured.

Before an installable release candidate is accepted it:

- restores in NuGet locked mode;
- builds and runs the full test suite;
- authenticates GitHub Actions to Azure through OIDC;
- asks Velopack to sign using Azure Artifact Signing;
- validates the Velopack feed, package contents and Authenticode signatures;
- generates `SHA256SUMS.txt`;
- creates a GitHub artifact attestation from those checksums;
- uploads the complete release directory as a short-lived Actions artifact.

The workflow still **does not publish the feed to end users**. Hosting/deployment remains a separate decision and permission boundary.

### 3. Release validation

`scripts/validate-velopack-release.ps1` verifies that packaging produced at least:

- `releases.<channel>.json` containing valid JSON;
- a full `.nupkg`;
- a Velopack Setup executable;
- a delta package when the caller requires one;
- no zero-length output files.

The validator opens the newest full package on every run and rejects user/signing material that must never ship inside application binaries, including `gamehours.db`, the temporary Azure signing metadata file, and private-key container/key extensions such as `.pfx`, `.p12`, `.p8` and `.key`.

When signing is enabled it additionally requires a valid Windows Authenticode signature on the newest Velopack Setup and every `GameHours*.exe` inside the newest full package, including `GameHours.Desktop.exe`.

Only after these checks does it write `SHA256SUMS.txt`. `scripts/package-windows.ps1` always invokes this validator before reporting success, so local, CI and signed release candidates share one quality gate.

## WPF/Velopack entry point

The packaged main binary is `GameHours.Desktop.exe`. Velopack lifecycle handling runs directly from `GameHours.Desktop.App.Main` before WPF initialization.

The WPF project follows Velopack's custom-entry-point structure:

- `App.xaml` is compiled as `Page` rather than `ApplicationDefinition`;
- `GameHours.Desktop.App` is the `StartupObject`;
- `[STAThread] Main` executes `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` first;
- normal `App.InitializeComponent()` / `App.Run()` occurs only afterwards.

This lets install/update hooks run without loading the normal WPF application. Auto-apply remains disabled so GameHours controls when the live tracker is shut down for an update.

## Update source configuration

A public release candidate must embed a read-only HTTPS update origin through `update-source.txt`. The release workflow reads:

```text
GAMEHOURS_UPDATE_SOURCE
```

from a repository variable and refuses to package if it is missing. The packaging script rejects HTTP, credentials, query strings and fragments for bundled sources. Local Velopack testing remains possible through the explicit runtime `GAMEHOURS_UPDATE_SOURCE` override rather than weakening the distributed package policy.

Never place a GitHub PAT, cloud secret or signing credential in `update-source.txt`, release notes or package contents.

## Code signing

GameHours targets **Azure Artifact Signing** (formerly Trusted Signing) for public Windows releases. Velopack integrates with it directly through `--azureTrustedSignFile`, allowing signing to happen at the correct points of the packaging lifecycle instead of post-processing generated packages.

The repository stores no PFX and no certificate password. GitHub Actions authenticates to Azure with OpenID Connect, so Azure trusts a federated GitHub identity and no long-lived Azure client secret is required.

The release workflow expects these repository variables:

```text
GAMEHOURS_AZURE_SIGNING_ENDPOINT
GAMEHOURS_AZURE_SIGNING_ACCOUNT
GAMEHOURS_AZURE_SIGNING_PROFILE
GAMEHOURS_UPDATE_SOURCE
```

and these protected GitHub secrets for the federated Azure identity identifiers:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
```

The Azure identity must have only the role needed to sign with the selected Artifact Signing certificate profile. Those external Azure resources and the federated credential are **manual configuration prerequisites**; repository code cannot provision them implicitly without taking ownership of an Azure subscription.

For Windows trust, Authenticode establishes publisher identity. The separate GitHub artifact attestation establishes build provenance — which repository, commit and workflow produced the checksummed artifacts. They complement each other; neither is treated as a substitute for the other.

## Production publishing is intentionally separate

The GameHours repository is public, so anonymous users can read public release assets. That makes GitHub Releases one possible future distribution surface, but repository visibility alone is not a deployment design.

The production update origin must be a read-only HTTPS location that installed clients can access without credentials and that exposes the Velopack release index/packages required by the installed channel. The desktop application must never embed a deployment credential merely to access updates.

## Delta updates and recovery

A stateless release runner needs previous remote release metadata/packages before packing if useful deltas are expected. CI already verifies the two-version local contract. Once the production host is selected, deployment should use the matching Velopack remote download/upload flow instead of manually copying individual feed files.

GameHours does not enable routine feed-driven downgrades. Normal recovery from a bad release is a higher-version signed hotfix. Exceptional startup-breaking recovery is a controlled reinstall of a known-good signed build after preserving user data where possible. Application binaries and user data deliberately live in separate directories; installed-machine preservation remains a manual gate.

The remote host, retention policy and deployment credentials belong to the deployment boundary, not the desktop client or packaging artifacts.

## Channels

GameHours currently ships only Windows x64, so `beta` and `stable` are sufficient channel names. If another OS/architecture is introduced, channel names must be revisited before public distribution so incompatible artifacts never share one feed.

Do not hard-code a different channel in the update client. The installed Velopack package carries its own channel identity.

## Remaining distribution work

- [x] reproducible local packaging command;
- [x] locked dependency restore and package output validation;
- [x] package-content guard against user/signing material;
- [x] SHA-256 manifest generation;
- [x] two-version/delta package smoke in Windows CI;
- [x] explicit WPF Velopack entry point;
- [x] HTTPS-only bundled update-source policy;
- [x] release workflow prepared for Azure OIDC, Artifact Signing and GitHub artifact attestations;
- [ ] provision Azure Artifact Signing account/profile and federated GitHub identity;
- [ ] select/configure the production read-only HTTPS update origin;
- [ ] validate the signed release workflow from `main`;
- [ ] add remote download/upload once the production host exists;
- [ ] real-machine clean install and `beta.1 -> beta.2` in-app update validation;
- [ ] validate signed install/update/recovery and evaluate SmartScreen with the signed binary.

See also [`UPDATES.md`](UPDATES.md), [`SUPPLY-CHAIN.md`](SUPPLY-CHAIN.md) and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md).
