# Windows distribution pipeline

GameHours uses Velopack for Windows packaging and updates. Distribution is split into explicit gates so automated checks can prove package quality without confusing that with a signed public release actually having been exercised.

## Current automated stages

### 1. Normal CI validation

Windows CI always performs:

```text
restore
  -> build
  -> test
  -> desktop publish smoke
```

Draft pull-request synchronizations stop there deliberately. Once a pull request is ready for review, and on `main` or manual dispatch, CI also exercises the Velopack update-chain path. The smoke builds two consecutive beta versions, embeds the public GitHub Releases update-source configuration, opens the generated package, requires the second build to contain a delta and validates the release feed.

The GitHub-source package path was explicitly exercised on Windows CI #719: build succeeded with zero warnings/errors, all 278 tests passed, the two packages validated and `0.0.1-ci.719.1 -> 0.0.1-ci.719.2` produced one delta package.

The same package/validator path was later exercised under Windows PowerShell 5.1 after the installed-machine smoke exposed that `System.IO.Compression.ZipFile` is not loaded automatically there. CI #729 passed the full two-version/delta package validation with the compatibility fix in place.

CI #736 additionally exercises the structural recovery path: silent install into a disposable root, uninstall, preservation of a sentinel outside the install root, reinstall and final cleanup. This proves install-root/user-data separation without pretending that it is a signed, user-facing recovery exercise.

### 2. Manual Package Windows workflow

`.github/workflows/package-windows.yml` is the public release workflow. It accepts an explicit SemVer and `beta`/`stable` channel and deliberately fails unless dispatched from `main` with the external signing prerequisites configured.

The workflow performs, in order:

1. validate release context and immutable version/tag policy;
2. require reviewed `release-notes/<version>.md`;
3. restore in NuGet locked mode;
4. build and run the full test suite;
5. restore the pinned Velopack CLI;
6. `vpk download github` for the previous release in the same channel;
7. authenticate GitHub Actions to Azure through OIDC;
8. package and sign through Azure Artifact Signing;
9. validate feed, package contents, expected update-source configuration, Authenticode and delta presence when a prior full release existed;
10. generate `SHA256SUMS.txt`;
11. create a GitHub artifact attestation from those checksums;
12. upload a short-lived Actions copy of the validated release directory;
13. only then run `vpk upload github --publish`.

The GitHub publication is intentionally the last side-effect. A failure in build, tests, signing, validation or attestation therefore does not publish a release.

This public workflow is **implemented but not yet operationally verified** because the Azure Artifact Signing account/profile and federated identity have not yet been exercised from `main`.

### 3. Release validation

`scripts/validate-velopack-release.ps1` verifies that packaging produced at least:

- `releases.<channel>.json` containing valid JSON;
- a full `.nupkg`;
- a Velopack Setup executable;
- a delta package when the caller requires one;
- no zero-length output files.

The validator opens the newest full package on every run and rejects user/signing material that must never ship inside application binaries, including `gamehours.db`, temporary Azure signing metadata and private-key extensions such as `.pfx`, `.p12`, `.p8` and `.key`.

When the package is configured for GitHub Releases it additionally requires exactly one `update-source.json`, no legacy `update-source.txt`, and an exact match with the expected public repository. The equivalent exact check remains available for legacy/static HTTPS packages.

When signing is enabled it requires a valid Windows Authenticode signature on the newest Velopack Setup and every `GameHours*.exe` inside the newest full package, including `GameHours.Desktop.exe`.

Only after these checks does it write `SHA256SUMS.txt`. `scripts/package-windows.ps1` always invokes this validator before reporting success, so local, CI and signed release candidates share one quality gate.

## WPF/Velopack entry point

The packaged main binary is `GameHours.Desktop.exe`. Velopack lifecycle handling runs directly from `GameHours.Desktop.App.Main` before WPF initialization.

The WPF project follows Velopack's custom-entry-point structure:

- `App.xaml` is compiled as `Page` rather than `ApplicationDefinition`;
- `GameHours.Desktop.App` is the `StartupObject`;
- `[STAThread] Main` executes `VelopackApp.Build().SetAutoApplyOnStartup(false).Run()` first;
- normal `App.InitializeComponent()` / `App.Run()` occurs only afterwards.

This lets install/update hooks run without loading the normal WPF application. Auto-apply remains disabled so GameHours controls when the live tracker is shut down for an update.

## Production update source: GitHub Releases

GameHours uses the public `Ayerdi/GameHours` GitHub Releases surface as the initial beta/stable distribution origin.

A production package contains:

```json
{"type":"github","repository":"https://github.com/Ayerdi/GameHours"}
```

in `update-source.json`. The Desktop constructs Velopack's `GithubSource` rather than treating a GitHub URL as a static HTTP feed.

Because the repository is public, installed clients do not contain or require a GitHub token. `beta` installations allow GitHub prereleases; `stable` installations consider stable GitHub releases only. Velopack still uses the installed package channel to select the corresponding `releases.<channel>.json` asset.

`GAMEHOURS_UPDATE_SOURCE` remains an explicit development/test override and may point at a fully-qualified local feed or a compatible HTTPS feed. An invalid override fails closed rather than falling back silently to the bundled source.

The older credential-free `update-source.txt` HTTPS format remains readable for compatibility, but new public GameHours packages use typed `update-source.json`.

## Code signing

GameHours targets **Azure Artifact Signing** (formerly Trusted Signing) for public Windows releases. Velopack integrates with it through `--azureTrustedSignFile`, allowing signing to happen at the correct packaging stages rather than post-processing generated packages.

The repository stores no PFX and no certificate password. GitHub Actions authenticates to Azure with OpenID Connect, so no long-lived Azure client secret is required.

### External Azure prerequisites

Before the first signed release, provision outside this repository:

1. an Azure Artifact Signing account;
2. completed Artifact Signing identity validation;
3. a certificate profile in that account;
4. an Entra application/service principal or user-assigned managed identity used only by the GitHub release workflow;
5. a federated identity credential that trusts this GitHub repository/workflow through OIDC;
6. an RBAC assignment granting that identity **`Artifact Signing Certificate Profile Signer`**.

For a public GameHours installer, use a **Public Trust** certificate profile rather than `Public Trust Test` or Private Trust. The test profile is suitable for development but is not publicly trusted by Windows.

For least privilege, scope the signer role to the certificate profile rather than to the resource group or subscription. The intended Azure scope is:

```text
/subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.CodeSigning/codeSigningAccounts/<account>/certificateProfiles/<profile>
```

Equivalent Azure CLI shape:

```text
az role assignment create \
  --assignee <object-id> \
  --role "Artifact Signing Certificate Profile Signer" \
  --scope "/subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.CodeSigning/codeSigningAccounts/<account>/certificateProfiles/<profile>"
```

The identity used by CI does **not** need Owner/Contributor merely to sign. Broader roles may be needed by the human/operator who initially creates the account/profile, but the runtime GitHub identity should receive only the signer role needed for the selected certificate profile.

### Recommended OIDC trust boundary

The workflow already requests `id-token: write` and authenticates with `Azure/login`; no Azure client secret is required. The federated credential should be restricted to this repository's `main` branch instead of trusting every branch in the repository.

For the current branch-based release workflow, use the GitHub OIDC subject equivalent to:

```text
repo:Ayerdi/GameHours:ref:refs/heads/main
```

and the recommended Azure audience:

```text
api://AzureADTokenExchange
```

If GameHours later moves release credentials behind a protected GitHub Environment, update the federated subject to the environment-based subject and configure environment protection rules at the same time; do not leave both a broad branch credential and an environment credential active without a reason.

### GitHub repository configuration

The release workflow expects these repository variables:

```text
GAMEHOURS_AZURE_SIGNING_ENDPOINT
GAMEHOURS_AZURE_SIGNING_ACCOUNT
GAMEHOURS_AZURE_SIGNING_PROFILE
```

`GAMEHOURS_AZURE_SIGNING_ENDPOINT` must be the endpoint for the Azure region that contains the Artifact Signing account/profile. A region/endpoint mismatch can cause signing to fail with authorization/Signer errors even when the identity and role assignment are otherwise correct.

The workflow expects these protected GitHub secrets/identifiers for the federated Azure identity:

```text
AZURE_CLIENT_ID
AZURE_TENANT_ID
AZURE_SUBSCRIPTION_ID
```

These are identifiers used by `Azure/login` with GitHub OIDC; they are not a certificate password or a long-lived client secret. The federated identity credential in Entra must be configured to trust the intended GameHours GitHub subject/repository before the workflow can authenticate.

For Windows trust, Authenticode establishes publisher identity. The separate GitHub artifact attestation establishes build provenance — which repository, commit and workflow produced the checksummed artifacts. They complement each other and neither substitutes for the other.

### First signed-release operator sequence

The first signed release is intentionally treated as an evidence exercise, not merely as a publication action.

1. Register/provision Artifact Signing in a supported Azure region and create the account.
2. Complete public identity validation in the Azure portal.
3. Create a **Public Trust** certificate profile.
4. Create the dedicated Entra workload identity for GameHours releases.
5. Add one GitHub federated credential restricted to `Ayerdi/GameHours` on `refs/heads/main` with audience `api://AzureADTokenExchange`.
6. Assign only `Artifact Signing Certificate Profile Signer` at the selected certificate-profile scope.
7. Configure the three signing variables and three Azure identifiers expected by `.github/workflows/package-windows.yml`.
8. Ensure the candidate version has reviewed `release-notes/<version>.md` and that its `v<version>` tag does not already exist.
9. Merge the reviewed foundation to `main`; do not bypass the workflow's `main` guard for convenience.
10. Dispatch **Package Windows** for a beta version.
11. Require the workflow to pass build/tests, Azure OIDC login, signing, Authenticode validation, checksum generation and GitHub attestation before publication.
12. On a clean/representative Windows machine, verify the published Setup signature and publisher, install it, observe SmartScreen behavior, then exercise a signed update to the next signed beta while a tracked game is running.
13. Verify session/data preservation and finally exercise the signed uninstall/reinstall recovery path.

Do not describe the signed release path as verified until those checks have actually been completed.

## Remote delta flow

The public workflow follows Velopack's intended remote lifecycle:

```text
GitHub Releases
      |
      v
vpk download github
      |
      v
vpk pack + sign + validate
      |
      v
attest checksums
      |
      v
vpk upload github --publish
```

`beta` download/upload uses GitHub prereleases; `stable` does not. If the download step recovered a previous full package, the new packaging step requires a delta. A first release is allowed to have no delta; CI has exercised the empty-history download path successfully.

Release versions are immutable. The workflow refuses to continue when the intended `v<version>` tag already exists instead of trying to overwrite an existing public release.

## Channels

GameHours currently ships Windows x64 only, so `beta` and `stable` are sufficient channel names.

Release metadata is kept consistent:

- `beta` requires a prerelease SemVer such as `0.2.0-beta.1` and creates a GitHub prerelease;
- `stable` rejects prerelease SemVer values and creates a normal GitHub release.

If another OS/architecture is introduced, channel names must be revisited before public distribution so incompatible artifacts never share one feed.

## Recovery policy

GameHours does not enable routine feed-driven downgrades. Normal recovery from a bad release is a higher-version signed hotfix. Exceptional startup-breaking recovery is a controlled reinstall of a known-good signed build after preserving user data where possible.

Application binaries and user data deliberately live in separate directories. Updating or reinstalling application binaries must not package, replace or delete `%LOCALAPPDATA%\GameHours`. The package validator rejects `gamehours.db`.

The structural separation is now covered by CI: install/uninstall/reinstall in a disposable Velopack root preserves a sentinel under `%LOCALAPPDATA%\GameHours`. The actual installed `0.2.0-beta.1 -> beta.2` update was also verified on a real Windows machine with the user's existing data and an active game. A final signed/user-facing recovery exercise remains separate because Authenticode and SmartScreen are not present in the unsigned local smoke.

## Remaining distribution work

- [x] reproducible local packaging command;
- [x] locked dependency restore and package output validation;
- [x] package-content guard against user/signing material;
- [x] SHA-256 manifest generation;
- [x] two-version/delta package smoke in Windows CI;
- [x] explicit WPF Velopack entry point;
- [x] typed public GitHub Releases update source;
- [x] remote `download -> pack -> upload` workflow implemented for GitHub Releases;
- [x] release workflow prepared for Azure OIDC, Artifact Signing and GitHub artifact attestations;
- [x] real-machine install over an existing version and `0.2.0-beta.1 -> 0.2.0-beta.2` in-app delta update validation;
- [x] automated silent install/uninstall/reinstall smoke proving install-root/user-data separation;
- [ ] provision Azure Artifact Signing account/profile and federated GitHub identity;
- [ ] validate the signed public release workflow from `main`;
- [ ] validate signed install/update/recovery and evaluate SmartScreen with a signed binary.

See also [`UPDATES.md`](UPDATES.md), [`SUPPLY-CHAIN.md`](SUPPLY-CHAIN.md), [`INSTALLED-UPDATE-VALIDATION-2026-08-29.md`](INSTALLED-UPDATE-VALIDATION-2026-08-29.md) and [`REAL-MACHINE-VALIDATION.md`](REAL-MACHINE-VALIDATION.md).
