# DVM Console Release Checklist

This checklist separates repository preparation, automated package evidence, and live operator validation. A release is not complete from a version change, local test run, or tag alone.

## 1. Prepare a focused release branch

- Start from the intended clean `neo` commit and initialize `fnecore` recursively.
- Confirm the release branch contains only reviewed release changes and that `fnecore` remains at the intended pinned revision.
- Set the same semantic version in `src/Directory.Build.props` and `packaging/macos/Info.plist`.
- Update version-sensitive tests, `CHANGELOG.md`, the current section and package names in `README.md`, and `docs/releases/v<version>.md`.
- Confirm the changelog comparison links point from the previous tag to the proposed tag and from the proposed tag to `HEAD`.
- Keep local measurements, operational codeplugs, key files, recordings, diagnostics, and other private release-working material out of the tracked repository.

## 2. Run deterministic source validation

```sh
git submodule update --init --recursive
dotnet restore dvmconsole.sln --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
dotnet test dvmconsole.sln --no-restore --disable-build-servers \
  --configuration Release --verbosity minimal \
  /m:1 /p:UseSharedCompilation=false

cargo fmt --manifest-path native/vocoder/Cargo.toml -- --check
cargo clippy --manifest-path native/vocoder/Cargo.toml --locked --all-targets -- -D warnings
cargo test --manifest-path native/vocoder/Cargo.toml --locked

cmake -S native/dvmaudio -B native/dvmaudio/build/release \
  -DCMAKE_BUILD_TYPE=Release
cmake --build native/dvmaudio/build/release --config Release
ctest --test-dir native/dvmaudio/build/release --output-on-failure
```

Record managed, Rust, and native test counts separately. Treat XAML compilation, deterministic settings/wire/PCM fixtures, and architecture-boundary tests as source evidence, not as proof of a live FNE, radio, or audio route.

## 3. Review the release-prep diff

- Confirm the working tree contains no private configuration, credentials, recordings, crash logs, temporary packages, or local measurement artifacts.
- If a local-only artifact was committed earlier on the release branch, removing it from the final tree is not sufficient; confirm it is absent from every commit that will be pushed.
- Confirm release notes describe only behavior supported by the source and validation evidence.
- Confirm public constructors, namespaces, XAML binding paths, settings paths and schema, FNE framing, native exports, and package names remain intentional.
- Review the exact commit messages, author identity, and release version before committing.
- Do not merge, push, tag, or publish until those actions are explicitly authorized.

## 4. Merge and tag only after approval

- Commit the reviewed release preparation on its focused branch.
- Merge the reviewed implementation and release-prep branches into a clean `neo` with the repository's approved non-fast-forward workflow.
- Verify the final `neo` tree, version metadata, release notes, and changelog after the merge.
- Create and push `v<version>` only after the final commit and tag targets have been approved.

## 5. Require all three package jobs

The tagged workflow must pass for:

- Apple Silicon macOS: `osx-arm64`
- Intel macOS: `osx-x64`
- Windows x64: `win-x64`

Read back each uploaded ZIP. Verify the expected version and architecture, legal notices, native vocoder, macOS audio shim where applicable, absence of Debug diagnostics and private/test configuration, and the correct package name. Both macOS jobs must pass packaged `.app` smoke; Windows must pass its headful Avalonia smoke.

## 6. Complete live acceptance separately

Before operational sign-off, validate on the intended environment:

- Connect and reconnect to the intended FNE and confirm protocol traffic and status behavior.
- Confirm sustained DMR, P25, and NXDN receive audio, stream isolation, jitter behavior, routing, output mute, and TAR recording.
- Confirm card, global, active-system, toggle, press-and-hold, and configured serial PTT, including microphone readiness and the talk-permit cue.
- Confirm fixed and system-default audio routes, Bluetooth transitions where used, recording playback, web playback, and clean shutdown/reload behavior.
- Confirm encrypted traffic and KMM key delivery against the intended FNE/KMF when those workflows are part of the deployment.

Managed tests and package smoke must not be presented as live radio, FNE, Bluetooth, serial, or hardware evidence.

## 7. Publish and read back the release

- Confirm the GitHub release title and body match `docs/releases/v<version>.md`.
- Confirm the release targets the approved tag and is not left as a draft unless a draft was requested.
- Confirm exactly three versioned ZIP assets are present and downloadable.
- Read back the published release body and asset names from GitHub.
- Record any remaining hardware-validation limitations explicitly.
