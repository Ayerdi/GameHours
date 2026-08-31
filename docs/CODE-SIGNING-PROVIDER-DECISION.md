# Public code-signing provider decision — 2026-08-29

GameHours requires a publicly trusted Authenticode signature before its first public Windows beta. This document records the provider-selection gate separately from the packaging implementation so an external-service limitation does not silently become an architectural constraint.

## Requirements

The selected route must provide:

- public Windows/AuthentiCode trust for the intended publisher identity;
- CA/B Forum-compliant private-key storage in hardware or a cloud HSM;
- a release flow that can be automated safely from GitHub Actions;
- compatibility with Velopack packaging-time signing (`--azureTrustedSignFile`, `--signParams`, or `--signTemplate`);
- timestamped signatures on the generated Setup and GameHours executables;
- no exportable private key or PFX committed to the repository;
- credentials scoped to release signing only;
- compatibility with the existing `Get-AuthenticodeSignature` release validation gate.

The provider must be selected before adapting `.github/workflows/package-windows.yml`; adding a generic signing abstraction before a concrete provider is known would add unnecessary configuration and branches.

## Azure Artifact Signing

**Engineering fit:** best current fit when eligible.

Advantages:

- native Velopack integration through `--azureTrustedSignFile`;
- cloud-managed key material;
- GitHub Actions can authenticate through short-lived OIDC rather than a stored Azure client secret;
- the CI identity can receive only `Artifact Signing Certificate Profile Signer` at certificate-profile scope;
- Public Trust is designed for publicly distributed Win32/Authenticode software.

Eligibility constraint from Microsoft as of 2026-08-29:

- Public Trust is available to organizations in the European Union and Microsoft's other supported organization regions;
- individual developers are eligible for Public Trust only in the United States and Canada;
- Public Trust Test and Private Trust are not substitutes for a public GameHours release.

Official references:

- https://learn.microsoft.com/azure/artifact-signing/quickstart
- https://learn.microsoft.com/azure/artifact-signing/concept-trust-models
- https://learn.microsoft.com/azure/artifact-signing/tutorial-assign-roles

If Azure is selected, GameHours must use GitHub's immutable OIDC subject because the repository was created after GitHub's 2026-07-15 rollout:

```text
repo:Ayerdi@128999164/GameHours@1341058538:ref:refs/heads/main
```

with audience:

```text
api://AzureADTokenExchange
```

Reference: https://docs.github.com/actions/reference/security/oidc

## SSL.com IV Code Signing + eSigner

**Engineering fit:** strong fallback for an individual publisher when Azure Public Trust is unavailable.

SSL.com currently offers Individual Validated (IV) code-signing certificates without requiring a registered business, and eSigner provides cloud-HSM signing intended for CI/CD. SSL.com documents GitHub Actions integration and programmatic signing.

Trade-offs:

- public trust and individual validation are suitable for an independent publisher;
- eSigner is designed for unattended CI/CD and avoids a physical token on the runner;
- unlike Azure OIDC, the documented integration relies on stored eSigner credentials/TOTP material, so GitHub release secrets and permissions need tighter protection;
- it is materially more expensive than the lowest-cost open-source certificate options.

Current published starting prices observed on 2026-08-29 are USD 129/year for IV Code Signing and a separate eSigner cloud-signing subscription starting at USD 15/month. Pricing and terms are external and must be rechecked before purchase.

Official/vendor references:

- https://www.ssl.com/products/software-integrity/code-signing/iv/
- https://www.ssl.com/products/software-integrity/signing-service/
- https://www.ssl.com/how-to/cloud-code-signing-integration-with-github-actions/

## Certum Open Source / Standard Code Signing in the Cloud

**Engineering fit:** attractive certificate cost, weaker fit for unattended GitHub-hosted releases.

Certum provides publicly trusted cloud code-signing certificates for natural persons, including an Open Source product intended for open-source developers. The Open Source cloud product was listed from EUR 49 but was out of stock when checked on 2026-08-29; Standard Code Signing in the Cloud was listed from EUR 209.

The cloud product uses Certum SimplySign. Certum's current requirements include its mobile application for access codes plus SimplySign Desktop on the signing computer. That interaction model is less suitable for an ephemeral unattended GitHub-hosted runner than Azure OIDC or a CI-focused signing API.

Do not choose it solely because of certificate price unless a safe, supported unattended release flow is demonstrated first.

Official/vendor references:

- https://shop.certum.eu/open-source-code-signing-on-simplysign.html
- https://shop.certum.eu/standard-code-signing-in-the-cloud.html
- https://support.certum.eu/en/installation-of-the-simplysign-applications/

## Current decision

No provider is selected yet because one fact changes the correct answer: the legal/publisher identity that will sign GameHours.

- If the publisher is an Azure-eligible organization, keep the existing Azure Artifact Signing/OIDC workflow.
- If the publisher is an individual not eligible for Azure Public Trust, prefer a CI-capable publicly trusted individual signing service; SSL.com IV + eSigner is the current leading candidate.
- Do not replace public trust with Azure Private Trust, a test certificate, or unsigned publication.

Once the publisher identity is confirmed, select one route, adapt only the signing boundary, exercise it on a single binary first, then run the complete Velopack signed-release gate.

## Validation after provider selection

The provider decision is not complete until all of the following pass:

1. a standalone test binary receives a valid timestamped Authenticode signature;
2. Velopack signs GameHours binaries and Setup during packaging;
3. `scripts/validate-velopack-release.ps1 -RequireAuthenticode` passes;
4. build/tests/checksums/GitHub attestation complete before publication;
5. a signed beta is published from `main`;
6. a representative Windows machine verifies publisher/signature and records SmartScreen behavior;
7. a signed-to-signed in-app update preserves tracking/session data;
8. signed uninstall/reinstall preserves user data as designed.
