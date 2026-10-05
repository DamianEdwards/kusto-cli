# Release, signing, provenance, and self-update

This document describes the release system used by `DamianEdwards/kusto-cli`.
It is intentionally local to this repository: the workflows originated in
`DamianEdwards/cli-repo-template`, but the trust rules, payload contents,
recovery procedures, and operational settings below are specific to Kusto CLI.

## Design goals

The release system is built around these invariants:

- Pull requests validate the same NativeAOT and packaged runtime paths used by
  releases.
- CI builds official release candidates once. Promotion does not rebuild them.
- The CI run, source commit, release tag, metadata, checksums, signatures, and
  attestations must all agree.
- Official Windows payloads fail closed unless every executable file is
  Authenticode signed and verified.
- Official macOS payloads fail closed unless every Mach-O executable/library is
  Developer ID Application signed with the hardened runtime and a secure
  timestamp, and Apple accepts notarization for both architectures.
- The macOS installer and self-updater verify each extracted native signature
  against the expected Apple Developer team before executing downloaded code.
- Official Linux and macOS archives are verified against a tag-bound Sigstore
  attestation before extraction or execution.
- Install and self-update operations replace only manifest-managed files,
  preserve unrelated files, and retain a rollback copy until the new CLI passes
  its packaged chart diagnostic.
- Mutable workflow state is isolated on dedicated branches; official tags and
  published releases are immutable.

## Release channels and versions

The repository publishes three channels:

- **Dev**: public prereleases produced directly by `ci.yml`, for example
  `0.3.2-pre.1.dev.1`. Dev archives are unsigned and unattested by design, but
  installers and self-update always verify their checksums and metadata.
- **Pre-release**: official signed and attested tags such as
  `0.3.2-pre.1.rel` or `0.3.2-rc.1.rel`.
- **Stable**: official signed and attested SemVer releases such as `0.3.1`.
  Stable releases become the repository's latest release.

Mutable state is stored as `version-state.json` on the workflow-managed
`release-state` branch:

```json
{
  "base": "0.3.2",
  "phase": "pre",
  "phaseNumber": 1,
  "devNumber": 0,
  "pending": "none"
}
```

`scripts/version.cs` converts that state into the next Dev and promotable
versions. Releasing a `pre` or `rc` build increments `phaseNumber`. Releasing an
RTM build advances the base version and selects the next phase from the
`default_post_release_phase` dispatch input or the
`DEFAULT_POST_RELEASE_PHASE` repository variable.

This repository currently sets `DEFAULT_POST_RELEASE_PHASE=rtm`. A maintainer
can override it for a particular promotion by selecting `pre`, `rc`, or `rtm`
when dispatching `publish-release.yml`.

## Workflow architecture

### Pull request validation

`.github/workflows/pr.yml` runs:

- change-aware PowerShell, shell, and C# file-app validation
- credential-free macOS signing/notarization helper tests
- the solution build and Linux x64 NativeAOT validation
- tests on Windows, Linux, and macOS
- NativeAOT validation on Windows x64, Linux ARM64, and macOS ARM64

The default-branch ruleset requires these checks:

- `validate-scripts`
- `build`
- `test (ubuntu-latest)`
- `test (windows-latest)`
- `test (macos-latest)`
- `publish-aot (windows-latest, win-x64)`
- `publish-aot (ubuntu-22.04-arm, linux-arm64)`
- `publish-aot (macos-latest, osx-arm64)`

### CI and promotable artifacts

`.github/workflows/ci.yml` runs on pushes to `main` and can also be dispatched
manually after a version-phase change.

It:

1. Reads `release-state`, or initializes it from existing releases if the
   branch does not yet exist.
2. Validates scripts, builds, and tests.
3. Publishes Dev and promotable archives for all six RIDs.
4. Runs packaged `_diag chart-self-test` checks on executable host/architecture
   combinations.
5. Merges each set into a release bundle.
6. Publishes a versioned public Dev release.
7. Advances `release-state` only after both bundles succeed.

CI, `bump-version.yml`, and `release.yml` use the same `release-state`
concurrency group with `queue: max`. This serializes every state writer without
discarding pending runs.

### Promotion dispatcher

**Start App Release** (`.github/workflows/publish-release.yml`) is the
maintainer entry point for an official release. It has its own
`publish-release` concurrency group because it dispatches **Finalize App
Release** (`release.yml`); putting the parent and child runs in the same group
can strand the child run behind its completed parent.

Before the production approval gate, it verifies that the selected CI run:

- used `.github/workflows/ci.yml`
- completed successfully
- ran for `main` in this repository
- was triggered by a `push` or `workflow_dispatch`
- has a valid source SHA
- produced the expected version and all six RIDs
- produced metadata whose `sourceCommit` matches that SHA
- produced archives matching `checksums.txt`

After approval, it creates an annotated `v{version}` tag at the CI SHA and
dispatches `release.yml` on that exact tag.

### Signing and release publication

**Finalize App Release** (`.github/workflows/release.yml`) validates the tag and
CI run again, downloads the promotable bundle, and performs the official
publication:

1. The `production` environment gates the Windows and macOS signing jobs.
2. Azure Artifact Signing signs every Windows executable payload:
   - `kusto.exe`
   - `libSkiaSharp.dll`
   - `libHarfBuzzSharp.dll`
   - `libsodium.dll`
3. The workflow verifies every signature and signer issuer chain.
4. macOS jobs sign every Mach-O file, including `kusto`, `libSkiaSharp.dylib`,
   `libHarfBuzzSharp.dylib`, and `libsodium.dylib`, with the same Developer ID
   Application identity, hardened runtime, and secure timestamp. Each job
   verifies the target architecture and every signature.
5. Each macOS payload is submitted to Apple as a temporary ZIP. Only an
   `Accepted` notarization result permits repacking the existing `.tar.gz`
   download. Native Intel and ARM64 runners smoke-test their signed archives,
   including the native chart-rendering stack.
6. Signed Windows and notarized macOS archives replace their unsigned CI
   equivalents. Neither job rebuilds the CLI or changes the payload manifest.
7. Checksums and release metadata are regenerated.
8. `actions/attest` creates tag-bound attestations for all six final archives.
9. The action's uncompressed JSONL bundle is published as
   `attestations.jsonl` for portable offline verification.
10. GitHub generates release notes from `.github/release.yml`.
11. The workflow advances `release-state`.

Signing is mandatory. Missing Azure or Apple settings, invalid identities,
signature failures, notarization failures/timeouts, or signed-archive smoke-test
failures prevent official release publication. Dev CI and PR builds do not use
Apple credentials and remain unsigned.

The macOS helper uses an isolated temporary Keychain, preserves other user
Keychain search entries, and removes the temporary Keychain and credential files
on exit. Notarization submission IDs, status responses, and available rejection
logs are retained for 90 days in
`macos-notarization-<rid>-<run-id>-<attempt>` Actions artifacts; these artifacts
never contain the P12 or API private key.

If release publication succeeded but state advancement did not, rerunning
`release.yml` on the same tag verifies the existing release, checksums, source
commit, six-RID inventory, and attestations before completing the state update.
It never deletes and recreates an existing official release.

## Release artifact layout

Each Dev or official release contains:

- `kusto-win-x64.zip`
- `kusto-win-arm64.zip`
- `kusto-linux-x64.tar.gz`
- `kusto-linux-arm64.tar.gz`
- `kusto-osx-x64.tar.gz`
- `kusto-osx-arm64.tar.gz`
- `checksums.txt`
- `release-metadata.json`

Official releases also contain `attestations.jsonl`.

Each archive contains:

- `kusto.exe` on Windows or `kusto` on Unix
- the platform's SkiaSharp, HarfBuzzSharp, and libsodium native sidecars
- `LICENSE`
- `THIRD-PARTY-NOTICES.md`
- `payload-manifest.json`

`payload-manifest.json` is a sorted list of every install-managed file except
the manifest itself. Packaging, installers, and self-update reject missing,
undeclared, or unsafe paths. The manifest is intentionally forward-compatible:
later releases may add or remove managed files without changing a hard-coded
updater allowlist. Official Windows releases sign and verify every declared
`.exe` and `.dll`.

macOS download names and payload contents remain unchanged. ZIPs are only
temporary notarization transport artifacts, not additional release assets.

## Trust model

### Windows

The PowerShell installer is the source of truth for:

- the expected signer subject
- allowed immediate issuer SHA512 thumbprints
- allowed parent intermediate issuer SHA512 thumbprints
- executable payload files that must be signed

`scripts/Generate-VerifyProvenance.ps1` extracts the shared validation region
from the installer and embeds it into Windows builds. This prevents the
installer and self-update trust policy from drifting.

Verification requires a valid Authenticode signature, timestamp, certificate
chain, signer subject, and allowed issuer. Root certificates are never accepted
as the issuer fallback.

### Linux and macOS

Official archives are attested by `release.yml` on the exact release tag.

- The CLI reads `attestations.jsonl` and verifies it locally with the `Sigstore`
  package.
- `install.sh` verifies the same bundle with Cosign 2.4.0 or newer. It uses the
  explicit new-bundle flag required by Cosign 2.x and the default bundle mode
  used by Cosign 3.x, then independently requires the signed DSSE envelope to
  contain SLSA v1 provenance for compatibility with Cosign versions that do not
  enforce the requested predicate type themselves.
- Both policies require the GitHub Actions OIDC issuer and this exact identity:

```text
https://github.com/DamianEdwards/kusto-cli/.github/workflows/release.yml@refs/tags/<tag>
```

Publishing the portable bundle avoids depending on GitHub's compressed
`bundle_url` response format at install time.

macOS adds Apple's Developer ID signatures and notarization to this existing
attestation policy; it does not replace Sigstore verification. All native
sidecars share the executable's signing identity so hardened-runtime library
validation does not require disabling library validation. NativeAOT does not
require JIT entitlements, and the workflow adds no permissive entitlements.

After attestation and manifest verification, the Unix installer and self-updater
check every declared Mach-O file before invoking its version command or chart
diagnostic. The policy requires a valid signature on every architecture, an
Apple trust anchor, a Developer ID Application certificate, the expected
developer team **`7B8Z7H3R6G`**, the hardened-runtime flag, and a secure timestamp.
This is the team on the verified `ghcp-spend-tray` release certificate, which
matches that project's configured signing fingerprint. Missing required native
sidecars, unsigned/modified files, other developers, and other Apple certificate
types are rejected.

The Unix installer is the source of truth: its `MACOS_SIGNING_TEAM_ID` constant
and verification functions are embedded verbatim as `verify-macos-provenance.sh`
in the CLI. The self-updater runs the embedded verification entry point using
its already-validated manifest file list, without requiring `jq`, Python,
Xcode, or a separately downloaded verification script. Release signing also
checks this same policy before submitting to Apple. Certificate renewal within
the same team does not require changing the trust pin; moving to another
developer team requires a deliberate installer/CLI trust-policy migration,
not merely changing a GitHub variable.

These checks verify the code signature locally; they do not perform an online
notarization lookup or invoke `spctl` as if the CLI were an app bundle.
Notarization acceptance is a release gate, and the tag-bound attestation covers
the final archive. Gatekeeper remains responsible for any separate system
notarization assessment.

Bare command-line executables, ZIPs, and tarballs cannot have notarization
tickets stapled to them. Apple records tickets for the submitted signed code;
Gatekeeper may need network access to obtain those tickets when assessing a
quarantined download. This pipeline does not promise offline Gatekeeper
assessment, ship a stapled DMG/installer, remove quarantine, or bypass system
security policy. This follows Apple's
[custom notarization workflow](https://developer.apple.com/documentation/security/customizing-the-notarization-workflow),
which explicitly documents that standalone binaries receive tickets but cannot
have them stapled.

Dev builds intentionally skip signature/attestation verification because they
are produced before official promotion. Checksum and release metadata
verification still run.

## Self-update

`kusto update` selects the highest SemVer candidate containing the current
platform archive:

- Dev builds can advance to any newer channel.
- Official prereleases can advance to newer official prereleases or stable.
- Stable builds select stable updates unless `--pre-release` or
  `include_prerelease_updates=true` opts in to official prereleases.
- `--stable-only` overrides the configured prerelease preference.

Useful commands:

```powershell
kusto update --check
kusto update --dry-run
kusto update
kusto update --pre-release
kusto update --stable-only
kusto config --set include_prerelease_updates=true
```

Checksums and metadata are always required. `--skip-provenance-checks` exists
for an explicitly trusted local test source; it skips code-signature and
attestation checks, but does not disable checksum verification. The Unix
installer's `--skip-provenance` has the same explicit scope.
`KUSTO_DISABLE_SELF_UPDATES=1` disables update checks.

Versions through `0.3.1` use the earlier fixed payload allowlist. If a later
release adds runtime files, users of those versions must run the current
installer once; subsequent updates use the manifest-driven contract.

The updater validates the downloaded version and packaged chart stack before
staging. It performs a manifest-managed file transaction:

- Existing managed files are copied to a backup before mutation.
- Unrelated install-directory files, including generated completion scripts,
  are preserved.
- Unix replaces managed files in-process.
- Windows starts a detached helper, waits for the active process to exit, then
  replaces managed files while holding the update lock.
- The backup remains until the new CLI passes `_diag chart-self-test`.
- Any failure restores the previous payload.

## Install script publication

Install scripts are published independently from CLI releases.

`.github/workflows/install-scripts.yml`:

1. Runs from `main` behind a `production` approval.
2. Requires all Azure signing settings.
3. Signs and verifies `install.ps1`.
4. Stages `install.ps1`, `install.sh`, snapshot-local `.gitattributes`, and the
   attestation workflow.
5. Generates `install-scripts.json` and `checksums.txt`.
6. Fast-forwards the workflow-managed `install-scripts` branch.
7. Creates an annotated `install-scripts-vYYYY.MM.DD.RUN.ATTEMPT` tag.
8. Dispatches `attest-install-scripts.yml` on that tag.

`.github/workflows/attest-install-scripts.yml` has a second `production`
approval. It verifies:

- the annotated tag belongs to the protected `install-scripts` history
- the recorded source commit belongs to `main`
- script checksums
- the PowerShell Authenticode signature and signer

It then attests both scripts and publishes an immutable, non-latest snapshot
release containing the scripts, manifest, checksums, and
`attestations.jsonl`.

Latest installer URLs are:

- `https://kusto.damianedwards.dev/install.ps1`
- `https://kusto.damianedwards.dev/install.sh`
- `https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.ps1`
- `https://raw.githubusercontent.com/DamianEdwards/kusto-cli/install-scripts/install.sh`

The Cloudflare Worker special-cases `kusto-cli` to serve the
`install-scripts` branch. Other repositories can continue using their legacy
standing `install-scripts` release tag.

## Repository configuration

### Production environment

The `production` environment allows:

- `main`
- `v*` tags
- `install-scripts-v*` tags

It has a required reviewer and currently permits administrator bypass. Release
tag creation, Windows/macOS signing, installer signing, and installer snapshot
attestation all use this environment as appropriate.

### Required environment secrets

These six Azure values remain mandatory for official releases and installer
publication:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `AZURE_SIGNING_ENDPOINT`
- `AZURE_SIGNING_ACCOUNT`
- `AZURE_CERT_PROFILE`

Azure holds the signing key material. GitHub stores only the identifiers used
for OIDC login and Azure Artifact Signing.

### Apple signing and notarization setup

Use the same credential names and authentication scheme as
[`DamianEdwards/ghcp-spend-tray`](https://github.com/DamianEdwards/ghcp-spend-tray).
Configure the following under **Settings > Environments > production** in
`DamianEdwards/kusto-cli`. Environment secrets are recommended so production
approval gates access to the private keys; repository secrets with the same
names also work if permitted by your policy.

| Name | GitHub setting | Value |
| --- | --- | --- |
| `MACOS_CERTIFICATE_P12` | Secret | Base64 of an exported **Developer ID Application** certificate together with its matching private key (`.p12`). |
| `MACOS_CERTIFICATE_PASSWORD` | Secret | Password used to encrypt/export that P12. |
| `MACOS_SIGNING_IDENTITY` | Variable | Exact certificate name, such as `Developer ID Application: Name (TEAMID)`, or its 40-character SHA-1 fingerprint. |
| `MACOS_NOTARY_KEY` | Secret | Full, unencoded contents of the App Store Connect **team API key** `.p8` file, including the PEM header/footer and newlines. |
| `MACOS_NOTARY_KEY_ID` | Secret | Key ID for that same API key. |
| `MACOS_NOTARY_ISSUER` | Secret | Issuer ID (UUID) for the App Store Connect team API key, not the Apple Developer Team ID. |

All six Apple settings are required for official macOS releases. They are mapped
to same-named environment variables only during the signing step. Installer
publication and ordinary CI do not require them. No `APPLE_ID`,
app-specific password, `MACOS_TEAM_ID`, provisioning profile, or Developer ID
Installer certificate is required by this workflow.
The certificate must belong to the pinned team `7B8Z7H3R6G`. Its identity can
change on renewal, but the team cannot be overridden through environment
variables. The signing helper rejects a certificate from another team before
producing release assets.

1. Have an active [Apple Developer Program](https://developer.apple.com/programs/)
   membership with permission to issue Developer ID certificates. In
   [Certificates, Identifiers & Profiles](https://developer.apple.com/account/resources/certificates/list),
   create a **Developer ID Application** certificate using a certificate signing
   request generated on your Mac, or reuse the valid certificate used by
   `ghcp-spend-tray` if appropriate for the same developer/team.
2. Install the certificate on the Mac that generated its private key. In
   **Keychain Access > login > My Certificates**, confirm the certificate expands
   to show its private key, then export the certificate and private key as a
   password-protected `.p12`. A downloaded `.cer`, an Apple Development
   certificate, or an intermediate CA certificate is not sufficient. Keep the
   default Apple certificate trust settings.
3. Create or reuse an authorized **team API key** in
   **App Store Connect > Users and Access > Integrations > App Store Connect API**.
   Retain its Key ID and Issuer ID, and securely save the downloaded `.p8` file
   (Apple only permits downloading the private key once). Individual API keys
   are not supported here because this workflow always supplies `--issuer`.
4. Add the secrets and variable to `production`. The existing environment must
   continue to allow `main` and `v*` tags, with required review configured.
   Configure the values separately in this repository; GitHub cannot export
   another repository's existing secret values.

Example configuration commands, run locally with the original credential files
(do not commit or print the P12 or P8 contents):

```bash
base64 < DeveloperIDApplication.p12 | tr -d '\r\n' |
  gh secret set MACOS_CERTIFICATE_P12 --repo DamianEdwards/kusto-cli --env production
gh secret set MACOS_CERTIFICATE_PASSWORD --repo DamianEdwards/kusto-cli --env production
gh variable set MACOS_SIGNING_IDENTITY --repo DamianEdwards/kusto-cli --env production \
  --body 'Developer ID Application: Name (TEAMID)'
gh secret set MACOS_NOTARY_KEY --repo DamianEdwards/kusto-cli --env production < AuthKey_KEYID.p8
gh secret set MACOS_NOTARY_KEY_ID --repo DamianEdwards/kusto-cli --env production
gh secret set MACOS_NOTARY_ISSUER --repo DamianEdwards/kusto-cli --env production
```

The commands without a supplied value prompt for it. The signing identity can
also be found with `security find-identity -v -p codesigning` on your Mac; use
the identity corresponding to the exported P12. Keep certificate rotation and
API-key revocation synchronized with these GitHub settings.

### Rulesets

- The default branch requires pull requests, linear history, one approval, and
  the status checks listed above.
- The `install-scripts` branch ruleset blocks deletion and non-fast-forward
  updates.
- The `install-scripts-v*` tag ruleset blocks deletion and tag rewrites.

The minimal installer rules allow the default `GITHUB_TOKEN` to create and
fast-forward the branch and create snapshot tags. If this repository later adds
non-admin writers, use a dedicated GitHub App or write deploy key for
publication, put that actor on the bypass list, and then restrict branch/tag
creation and updates to that actor.

### Repository variables

- `DEFAULT_POST_RELEASE_PHASE=rtm`
- `MACOS_SIGNING_IDENTITY` (prefer the `production` environment variable described
  above; a repository variable with the same name is also supported)

## Maintainer procedures

### Publish normal development output

Merge a PR to `main`, or manually dispatch `ci.yml`. Confirm all six Dev and
promotable jobs, both bundle jobs, and the release-state update succeed.

### Change the release phase

Dispatch `bump-version.yml` with:

- `version_bump`: `auto`, `patch`, `minor`, or `major`
- `phase`: `pre`, `rc`, or `rtm`

Then merge a change or dispatch `ci.yml` to produce artifacts from that state.

### Publish an official release

1. Identify the successful `ci.yml` run to promote.
2. Manually run **Start App Release** (`publish-release.yml`) on `main` with
   that run ID.
3. Approve the production tag-publication deployment.
4. Confirm the annotated tag points to the CI source SHA.
5. Approve the production signing deployment in **Finalize App Release**.
6. Confirm the release, attestations, and release-state advancement.

Both macOS signing jobs and Windows signing must succeed before a new release
can be attested or published.

For the initial rollout, publish the first signed/notarized **stable** CLI
release before publishing the stricter Unix installer. Historical unsigned
official macOS releases will fail the new installer's signature checks; only
Dev builds and the explicit provenance-bypass flags skip them. Existing older
CLI installations can acquire the first signed release using their existing
attestation-based updater, and then subsequent updates enforce the Apple
signature policy too.

**Finalize App Release** normally starts automatically. If tag dispatch fails
after the tag is pushed, run it manually on the existing tag with the original
CI run ID and phase. Do not recreate the tag.

Promote a CI run whose source commit contains the current workflow files.
GitHub correctly rejects a workflow token attempting to create a tag at an
older commit when that operation would introduce different workflow content.

### Recover a notarization failure or timeout

Download the failed job's `macos-notarization-<rid>-<run-id>-<attempt>` artifact
and inspect `<rid>-submission.json` for the submission ID. Apple can continue
processing after the helper's 60-minute wait expires, particularly for a team's
first submissions. A timeout is not a rejection or cancellation. Check the
existing submission before starting another release attempt:

```bash
xcrun notarytool info '<submission-id>' --key AuthKey_KEYID.p8 \
  --key-id '<key-id>' --issuer '<issuer-uuid>' --output-format json
xcrun notarytool log '<submission-id>' --key AuthKey_KEYID.p8 \
  --key-id '<key-id>' --issuer '<issuer-uuid>' notarization-log.json
```

An `Invalid` result must be investigated using the retained rejection log or
`notarytool log`. Fix credential/certificate configuration or the reported
payload issue before retrying. A workflow rerun signs and submits fresh
payloads; it does not resume an old submission or publish unsigned artifacts.
Use the existing release tag and original CI run ID, never recreate the tag.

### Publish installers

1. Manually run **Start Install Script Release** (`install-scripts.yml`) on
   `main`.
2. Approve signing and branch/tag publication.
3. Approve **Finalize Install Script Release** when it starts automatically for
   the generated snapshot tag.
4. Verify the vanity and raw branch URLs return the same script bytes.

If the automatic dispatch fails after the snapshot tag is pushed, run
**Finalize Install Script Release** manually on that existing tag. Do not
recreate the tag.

### Clean old snapshots

`releases-cleanup.yml` runs weekly and can be dispatched manually. It keeps the
latest five Dev releases and latest five installer snapshot releases by
default. Dev cleanup removes generated tags; installer cleanup preserves the
protected snapshot tags.

## Verification commands

Verify an official archive attestation:

```powershell
$tag = "v0.3.1"
$identity = "https://github.com/DamianEdwards/kusto-cli/.github/workflows/release.yml@refs/tags/$tag"
gh release download $tag --repo DamianEdwards/kusto-cli
gh attestation verify .\kusto-win-x64.zip `
  --repo DamianEdwards/kusto-cli `
  --cert-identity $identity
```

Verify Windows payload signatures:

```powershell
Expand-Archive .\kusto-win-x64.zip .\kusto-win-x64
.\scripts\Verify-WindowsBinaryIssuer.ps1 `
  -PayloadDirectory .\kusto-win-x64 `
  -InstallerScriptPath .\scripts\install\install-kusto-cli.ps1
```

Verify macOS payload signatures after downloading and verifying the archive
attestation:

```bash
mkdir -p kusto-osx-arm64
tar -xzf kusto-osx-arm64.tar.gz -C kusto-osx-arm64
for file in kusto-osx-arm64/kusto kusto-osx-arm64/*.dylib; do
  codesign --verify --strict --verbose=2 "$file"
  codesign --display --verbose=4 "$file"
done
./kusto-osx-arm64/kusto _diag chart-self-test --output /tmp/kusto-chart.png
```

Signature details should show the expected `Developer ID Application` authority
and TeamIdentifier `7B8Z7H3R6G`, the `runtime` flag, and a secure Timestamp.
Notarization is confirmed by the retained `Accepted` status for each submission;
`codesign --verify` alone does not verify notarization.

Exercise negative provenance cases:

```powershell
.\scripts\Test-InstallerProvenance.ps1 `
  -Scenario UnsignedBinary `
  -BinaryPath .\artifacts\unsigned\kusto.exe

.\scripts\Test-InstallerProvenance.ps1 `
  -Scenario ChecksumMismatch `
  -ArchivePath .\kusto-win-x64.zip `
  -ChecksumsPath .\checksums.txt
```

Verify stable installation and update:

```powershell
irm https://kusto.damianedwards.dev/install.ps1 | iex
kusto --version
kusto update --check
kusto update
```

## Workflow helpers

Release support is implemented by:

- `scripts/Publish-NativeAsset.ps1`
- `scripts/merge-release-bundle.cs`
- `scripts/update-release-bundle-metadata.cs`
- `scripts/expand-windows-release-assets.cs`
- `scripts/compress-windows-release-assets.cs`
- `scripts/sign-macos-release.py`
- `scripts/test_macos_signing.py`
- `scripts/write-install-scripts-manifest.cs`
- `scripts/version.cs`
- `scripts/publish-branch-content.sh`
- `scripts/update-dev-release.sh`
- `scripts/Generate-VerifyProvenance.ps1`
- `scripts/Verify-WindowsBinaryIssuer.ps1`
- `scripts/Test-InstallerProvenance.ps1`
- `scripts/Verify-PowerShellSyntax.ps1`
- `scripts/verify-shell-syntax.sh`

There is no Homebrew formula, Winget/Scoop manifest, NuGet tool package,
apt/rpm package, container image, or SBOM publication in this release system.
Those would be separate distribution features.
