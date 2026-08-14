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
| M0 | Repository and ledger setup | Complete | Dedicated branch and checked-in ledger exist. |
| M1 | Cross-platform solution skeleton | Complete | New projects build without changing the legacy WPF project. |
| M2 | Configuration/core extraction | Complete | Codeplug, key, and alias models/loaders are covered by tests. |
| M3 | FNE core modernization | Complete | FNE protocol source builds against .NET 8 with offline smoke tests. |
| M4 | Software vocoder backend | Complete | `libvocoder` loads and encode/decode vectors pass on Apple Silicon with tracked tests. |
| M5 | Audio and platform services | In progress | Platform-neutral audio devices, capture, routing, and PTT contracts exist; native backends remain. |
| M6 | Avalonia application shell | In progress | Shared Avalonia shell starts on the desktop target and shows codeplug-derived system/channel status; live FNE status is not connected yet. |
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
- Added the first .NET 8 projects: configuration core, vocoder abstraction, source-based FNE wrapper, bootstrap app, and core tests.
- Added `src/DvmConsole.Rebuild.sln` as the rebuild entry point.
- Reserved `configs/codeplug_testing.yml` for explicit live-FNE/live-codeplug validation; it is not used by automated tests.
- Extracted the legacy key-file and radio-alias loaders into the cross-platform core and covered them with repository fixtures.
- Added offline FNE protocol tests for RTP headers, FNE extension headers, fragmentation/reassembly, and opcode construction.
- Added tracked software-vocoder frame validation and native encode/decode tests for DMR AMBE and P25 IMBE; documented the external `dvmvocoder` build.
- Added platform-neutral audio contracts for device enumeration, PCM capture/playback, PTT state, and a tested assembler for arbitrary callback sizes to 160-sample vocoder frames.
- Added the first Avalonia desktop shell for the shared `net8.0` desktop target. It loads a codeplug, shows systems/zones/channels, and labels FNE connections offline until the connection service is migrated.
- Added an explicit FNE client lifecycle service that maps legacy system configuration, resolves endpoints, configures `fnecore.FnePeer`, and publishes disconnected/starting/authentication/configuration/connected/faulted status states without auto-connecting.
- Verified three configuration tests, four FNE protocol tests, the bootstrap against `configs/codeplug.example.yml`, the full solution with `/m:1`, and the native vocoder smoke harness.
- Recorded the FNE wrapper's .NET 8 compatibility warnings as follow-up modernization debt; the original `fnecore` source remains unchanged.

## Commit ledger

| Commit | Purpose |
|---|---|
| `7dc3cc3` | Initial branch and migration ledger. |
| `d298080` | Cross-platform .NET 8 project skeleton and configuration/vocoder boundaries. |
| `056870b` | Legacy key and alias loading extracted into the cross-platform core. |
| `6142c29` | Offline FNE protocol test project and solution integration. |
| `808d66a` | Software-vocoder frame validation, native tests, and build documentation. |
| `dc4da04` | Platform-neutral audio contracts and PCM frame assembler. |
| `7364d22` | Initial Avalonia macOS/Windows desktop shell. |
| `0fd5f7b` | Explicit FNE connection lifecycle service and offline tests. |

## Verification log

| Check | Result |
|---|---|
| Host architecture | Apple Silicon / `arm64` |
| .NET SDK | 9.0.300; .NET 8 SDK also installed |
| Native vocoder CMake build | Passed in temporary verification build |
| Native vocoder .NET smoke test | Passed on Apple Silicon; 0 decode errors |
| Core configuration tests | 3 passed |
| FNE protocol tests | 4 passed on Apple Silicon; legacy compatibility warnings remain |
| Software vocoder tests | 4 passed on Apple Silicon with `DVMVOCODER_LIBRARY=/private/tmp/dvmvocoder-build/libvocoder.dylib` |
| Audio framing tests | 3 passed on Apple Silicon |
| Rebuild solution after audio boundary | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` (14 legacy FNE warnings) |
| Avalonia desktop shell | Built cleanly; launch check remained running as expected until the test process was interrupted |
| FNE connection service tests | 4 passed without opening a network connection |
| Rebuild solution | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` |
| Bootstrap config validation | Passed with `configs/codeplug.example.yml` |
| Live testing config | Present locally and ignored by Git |
| Legacy application build on macOS | Not attempted; WPF is Windows-only |
