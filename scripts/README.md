# Build and validation scripts

Run commands from the repository root. Use Bash on macOS/Linux and PowerShell
on Windows. Python helpers use the standard library. Build prerequisites and
supported targets are in the [build guide](../docs/user-guide/Getting%20Started/02-Building.md).

| Task | Entry points |
|---|---|
| Publish an unpackaged desktop build | `publish-desktop.sh` / `publish-desktop.ps1` |
| Package an existing publish directory | `package-desktop.sh` / `package-desktop.ps1` |
| Build a portable Linux AppImage | `build-linux-portable.sh` |
| Verify an unpackaged build, including native libraries | `verify-publish.sh` / `verify-publish.ps1` |
| Compare a ZIP with its source inventory | `verify-package.py --archive ... --publish-root ... --rid ...` |
| Check common publish files and documentation | `verify-package.py --publish-only --publish-root ... --rid ...` |
| Check archive size | `verify-package-size.py` |
| Smoke-test a macOS app or Linux AppImage | `smoke-desktop-macos.sh` / `smoke-desktop-linux-appimage.sh` |
| Test Linux distribution compatibility | `smoke-desktop-linux-container.sh` |
| Probe the native PipeWire library | `probe-linux-pipewire.sh` |

The native publish verifiers also run the shared Python inventory check. Run
the native verifier when checking binary architecture and dependencies; the
Python inventory check covers files and documentation.
Publish and package commands validate their own inputs so they can be used
independently. Verification after extraction checks the delivered artifact,
not just the directory it was built from.

`package-desktop-linux-appimage.sh`, `create-reproducible-zip.py`,
`validate-package-target.py`, and `invoke-smoke-with-timeout.ps1` support those
entry points. `release_metadata.py` handles release names, notes, checksums and
SBOM metadata. Keep publication separate from local build commands.

Run every helper test with:

```sh
python3 -m unittest discover -s scripts/tests -p 'test_*.py'
```

For source checks:

```sh
scripts/verify-source-headers.sh
scripts/verify-public-privacy.sh
python3 scripts/verify-documentation.py --root docs/user-guide
scripts/verify-format.sh
```

For an affected managed layer, run its test project in Release configuration.
Before integration, run the complete solution:

```sh
dotnet restore dvmconsole.sln --locked-mode -p:Configuration=Release
dotnet test dvmconsole.sln --no-restore --disable-build-servers \
  --configuration Release /m:1 /p:UseSharedCompilation=false
```

CI runs shared header/privacy/documentation checks once, formatting on one
matrix target, and all helper and solution tests on each of the six targets.
Native tests, package smoke tests, reproducibility, checksums, and verification
of uploaded assets each cover a different part of the release. Device and live
radio testing are still required after automated checks pass.

## iOS builds

`build-ios.sh` supports simulator, unsigned device, private signed device, and
App Store builds. Start with a simulator build on an Apple Silicon Mac:

```sh
bash scripts/build-ios.sh Debug simulator
```

Use `Release unsigned-device` to check device compilation without signing.
To install a private build, use `Release signed-private-device` with a signing
identity and provisioning profile that cover your device.

`write-ios-build-manifest.py` records and verifies the build inputs. The iOS CI
workflow uses `run-ios-simulator-smoke.py` and `test-ios-native.py` to check
iPhone/iPad launches, codecs, audio, recording, connections, Studio, Help, and
accessibility. Their helper tests run with the other script tests. Supply signing
credentials outside the repository.

For a TestFlight/App Store package, install an Apple Distribution signing identity
and an App Store Connect provisioning profile for the app, then run:

```sh
export NEO_IOS_SIGNING_IDENTITY='Apple Distribution: YOUR NAME (TEAM ID)'
export NEO_IOS_PROVISIONING_PROFILE='YOUR PROFILE UUID'
export NEO_IOS_BUILD_NUMBER=3 # Use a new number for each upload.
bash scripts/build-ios.sh Release app-store
```

The IPA and checksum are under `artifacts/ios/ios-arm64/Release/app-store/package`.
The SDK also creates an Xcode archive; retain it and its debugging symbols with
the matching source revision. Upload the IPA separately using Transporter, then
complete TestFlight metadata and export-compliance questions in App Store Connect.
External testing may require beta review. The script never uploads or publishes.

## Public script scope

This directory contains the tools used to build, package and verify distributed
binaries, together with tests for those tools. Native and simulator smoke checks
verify that builds launch and exercise selected features. They do not measure
capacity under sustained load.

Keep standalone benchmarks, profiling experiments, screenshot automation and
maintainer notes outside the tracked release inputs.
Check CI and script callers before removing a public helper.

## Release build ownership

For a release, create the annotated version tag on the reviewed `neo` commit,
then push the branch and tag together:

```sh
git push --atomic origin neo v<version>
```

The tag runs the full build, test, signing and publication flow using its own
fresh artifacts. The paired branch run performs source checks and skips the
expensive jobs when the matching annotated version tag points to the same commit.
Ordinary `neo` pushes still run the full checks. Pushing the branch first can start
a separate full build before the tag exists. Lightweight tags do not suppress
branch builds.

## GitHub TestFlight delivery

**Build and package** runs iOS Release device compilation and iPhone/iPad
simulator checks alongside the six desktop targets after source validation.
Tagged releases require those iOS checks as well as the desktop release gates.

`testflight.yml` runs after **Build and package** succeeds for a push to `neo`
or a release tag. It requires a successful **Delivery qualification** job, which
checks every desktop and iOS result and, for tags, completed release publication.
Before accessing signing credentials, delivery also checks that the tested commit
is an ancestor of the current `neo` branch.
It checks out that exact commit, builds the signed App Store IPA, validates it,
and uploads it without repeating the completed iOS validation jobs. PR runs and branch runs that defer to a release tag do not trigger delivery. The workflow must
exist on the repository's default branch for GitHub's `workflow_run` trigger to
operate; if the default branch differs from `neo`, install the workflow there too.

Create a GitHub environment named `testflight`. Limit deployment branches to the
repository's default branch (the `workflow_run` execution ref). The workflow also
requires that the upstream run is a successful same-repository push to `neo`
or a release tag, with completed delivery qualification.
Add these environment secrets using GitHub's encrypted secret settings:

| Secret | Value |
| --- | --- |
| `IOS_DISTRIBUTION_P12_BASE64` | Base64 of an exported Apple Distribution certificate **and private key** (.p12) |
| `IOS_DISTRIBUTION_P12_PASSWORD` | Password protecting that export |
| `IOS_APP_STORE_PROFILE_BASE64` | Base64 of the matching App Store Connect provisioning profile |
| `ASC_API_KEY_P8_BASE64` | Base64 of an App Store Connect team API key (.p8) |
| `ASC_API_KEY_ID` | That API key's ID |
| `ASC_API_ISSUER_ID` | The team API key issuer ID |

Use an API key with the Developer role for build uploads. Base64 is encoding, not
protection: never commit these values or put them in workflow variables. Keep the
original certificate, key and profile files outside the repository. Renew the
certificate/profile before expiry. An optional environment required reviewer can
pause each delivery; omit it for automatic upload after the checks pass.

The runner uses Xcode 26.6, the SDK in `global.json`, iOS workload 10.0.401 and
Rust 1.85.0. Update these together when changing the Apple build toolchain.
`upload-testflight.sh` refuses to run outside a disposable GitHub-hosted runner.
It uses a temporary keychain and removes signing inputs on success or failure.
Only dSYMs are retained as GitHub artifacts, for 90 days. Download them if you
need to diagnose crashes from those builds after that period. Full archives
contain provisioning material and are not uploaded as GitHub artifacts.

Build numbers are `1000 + workflow run number * 100 + run attempt`, keeping the
initial manually uploaded builds below the CI range and giving retries fresh
numbers. Rerun the **latest** delivery after a failure; do not rerun an older
commit after a newer build has shipped. GitHub concurrency serializes delivery
and can replace pending runs with newer ones. This delivers successful updates,
not necessarily every intermediate push.

Success means Apple accepted the upload. Processing, encryption declarations,
external group selection, beta review, and tester notification remain App Store
Connect steps. This workflow does not submit a public App Store release or change
legal declarations. No Apple password or Organizer interaction is needed.

To enable delivery, install the workflow on the required branches, configure the
environment and secrets, then push an update to `neo`. Verify the first hosted
run and its processed build in TestFlight before relying on unattended delivery. `workflow_run` uses
the default-branch workflow definition, so only trusted maintainers should be
able to modify the default branch and `neo`.

## Developer ID signing and notarization for macOS

Keep ARM64 and Intel packages separate. Build/package and reproduce the unsigned
ZIP first with the existing scripts. Signing is a delivery stage after those
checks; timestamped signatures and Apple's tickets are not byte-reproducible.

With a Developer ID Application identity in your keychain, run:

```sh
export NEO_NOTARY_PROFILE=your-notarytool-profile
python3 scripts/notarize-macos.py unsigned.zip signed.zip \
  --identity 'Developer ID Application: Your Name (TEAMID)' \
  --evidence /path/to/private/notarization-evidence
```

Alternatively set `ASC_API_KEY_FILE`, `ASC_API_KEY_ID` and `ASC_API_ISSUER_ID`.
Use a new output path. The input ZIP remains unchanged. The script signs the
code directory and bundle, requires Hardened Runtime, submits through
`notarytool`, requires Accepted status, staples the ticket, and verifies both
the signature and Gatekeeper acceptance after extracting the final ZIP.
The apphost receives the JIT entitlement required by .NET and the audio-input
entitlement required for microphone access under Hardened Runtime. The script
reads back the signed entitlements and checks the microphone usage description
before submission. Debugger access and disabled library validation are not enabled. Bundle data
lives in `Contents/Resources`, while managed and native code stays in MacOS.
`ditto` preserves the extended signatures on loose managed assemblies.

The `notarize-macos` CI jobs follow successful matrix/reproducibility and advisory
checks for trusted pushes. PR builds never access signing secrets. Restrict the
`macos-distribution` GitHub environment to branch `neo` and release tags `v*`;
each signing job also checks that its commit belongs to `neo`. Configure:

- `MACOS_DEVELOPER_ID_P12_BASE64`: Developer ID Application certificate/private key.
- `MACOS_DEVELOPER_ID_P12_PASSWORD`: its export password.
- `ASC_API_KEY_P8_BASE64`, `ASC_API_KEY_ID`, `ASC_API_ISSUER_ID`: notary API access.

These are environment secrets, not repository files. The earlier Apple
Distribution certificate for iOS/TestFlight cannot replace Developer ID.
CI imports the identity into a disposable keychain and removes it on exit.
Missing credentials, rejected notarization or failed signed-package smoke checks
block publication. SBOMs, checksums and attestations describe the final signed
ZIPs; unsigned reproducibility evidence remains separate. Signing requires
network access for Apple's secure timestamp service and notarization endpoints.

Submission evidence includes standard output, standard error and the exit code,
even when Apple returns no JSON. A failure to retrieve the diagnostic log does
not replace the submission error. The script does not automatically resubmit
an upload whose outcome is unknown.

If a submission times out, inspect its saved request ID with `notarytool info`
and `notarytool log` before resubmitting. A failed run does not produce a final
signed ZIP. Do not describe packages as notarized based on signature checks alone.
