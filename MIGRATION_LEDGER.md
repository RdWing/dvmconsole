# dvmconsole macOS Rebuild Ledger

## Scope

Rebuild the `r01a02_dev` desktop dispatch console for Apple Silicon macOS while retaining a Windows path. The new application will use a cross-platform .NET UI; the existing WPF application remains the Windows reference and fallback during migration.

## Decisions

- Target macOS Apple Silicon first: `osx-arm64`.
- Retain Windows support in the new application, initially targeting `win-x64`.
- Use the software `libvocoder` backend by default.
- Preserve a future `AMBE.DLL` backend behind a platform-neutral vocoder interface.
- Keep the rebuild on branch `codex/macos-cross-platform-rebuild`.
- Make each milestone a focused commit with an informative message.

## Plan

| ID | Milestone | Status | Exit criteria |
|---|---|---|---|
| M0 | Repository and ledger setup | In progress | Dedicated branch and checked-in ledger exist. |
| M1 | Cross-platform solution skeleton | Pending | New projects build without changing the legacy WPF project. |
| M2 | Configuration/core extraction | Pending | Codeplug models and loading are covered by tests. |
| M3 | FNE core modernization | Pending | FNE protocol source builds against .NET 8 with smoke tests. |
| M4 | Software vocoder backend | Pending | `libvocoder` loads and encode/decode vectors pass on Apple Silicon. |
| M5 | Audio and platform services | Pending | macOS audio devices, capture, routing, and PTT abstractions work. |
| M6 | Avalonia application shell | Pending | macOS and Windows shells start and show connection status. |
| M7 | Feature migration | Pending | RX/TX, patching, tones, TAR, settings, and history reach parity. |
| M8 | Packaging and integration handoff | Pending | Signed macOS artifact, Windows artifact, docs, and integration notes exist. |

## Working assumptions

- The existing `dvmconsole/` WPF project is not modified until the new core boundaries are proven.
- Native dependencies are loaded through explicit runtime-aware adapters rather than hard-coded Windows filenames.
- Existing codeplug/configuration compatibility is more important than preserving the current WPF view structure.

## Activity log

### 2026-08-14

- Cloned `r01a02_dev` with the `fnecore` submodule into the workspace.
- Audited the branch: WPF/.NET 8 application, `fnecore` on `netcoreapp3.1`, Windows-specific audio and keyboard code, and `libvocoder`/`AMBE.DLL` interop.
- Verified the current `dvmvocoder` source builds as `libvocoder.dylib` on this Apple Silicon host.
- Created branch `codex/macos-cross-platform-rebuild`.

## Commit ledger

| Commit | Purpose |
|---|---|
| _pending_ | Initial branch and migration ledger. |

## Verification log

| Check | Result |
|---|---|
| Host architecture | Apple Silicon / `arm64` |
| .NET SDK | 9.0.300; .NET 8 SDK also installed |
| Native vocoder CMake build | Passed in temporary verification build |
| Legacy application build on macOS | Not attempted; WPF is Windows-only |
