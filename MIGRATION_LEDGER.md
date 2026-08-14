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
| M5 | Audio and platform services | In progress | Platform-neutral audio devices, capture, routing, and PTT contracts exist; macOS CoreAudio, Windows NAudio, and a manual PTT source are implemented, while keyboard/hardware PTT adapters remain. |
| M6 | Avalonia application shell | In progress | Shared Avalonia shell starts on the desktop target, shows codeplug-derived system/channel status, and exposes explicit live FNE connect/disconnect status; feature parity remains. |
| M7 | Feature migration | In progress | Platform-neutral FNE traffic, DMR/P25 RX packet extraction/selection, and PCM/vocoder frame boundaries exist; desktop device/channel wiring, TX routing, P25 security, NXDN/analog media, patching, tones, TAR, settings, and history remain. |
| M8 | Packaging and integration handoff | In progress | Reproducible unsigned framework-dependent macOS and Windows publish paths exist; signing, installer packaging, and integration handoff remain. |

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
- Added a bounded `DvmConsole.FneProbe` utility for explicit live testing; it does not run from the desktop app and redacts credentials/raw packets from output.
- Added an Apple Silicon CoreAudio/AudioUnit native shim, managed audio backend, streaming PCM rate converter, and `DvmConsole.AudioProbe`; device enumeration and a two-second default input/output stream test pass on this Mac.
- Added deterministic PCM rate-converter tests, bringing the audio test count to six, and kept the native audio library outside the managed solution build so Windows remains buildable while its backend is pending.
- Wired the Avalonia shell to the FNE lifecycle service with explicit Connect/Disconnect commands, per-system status updates, UI-thread dispatch, and clean window-shutdown disposal; startup remains idle until the operator connects.
- Added the Windows NAudio/WinMM audio implementation and runtime backend factory. It compiles with the cross-platform solution, but a physical Windows audio-device run remains outstanding.
- Added a copied, platform-neutral FNE traffic event boundary for DMR, P25, NXDN, and analog frames, plus streaming PCM-to-vocoder and vocoder-to-PCM processors for the next RX/TX routing stage.
- Added a lifecycle-safe manual PTT source with transition tests as the host-controlled foundation for future keyboard and hardware PTT adapters.
- Added a reproducible desktop publishing script and publishing guide. It builds framework-dependent `osx-arm64` and `win-x64` outputs and places the macOS CoreAudio shim beside the macOS managed output.
- Added the first end-to-end DMR RX media slice: the legacy 55-byte DMR FNE voice-packet layout is mapped to three AMBE codewords, decoded through the software-vocoder boundary, and written to the platform-neutral playback boundary. Non-voice frames are ignored; P25 DFSI reconstruction and call/channel selection remain deliberately above this reusable session.
- Added reusable DMR receive selection and routing: destination ID, zero-based FNE timeslot, and voice frame type are matched before decode, and selected packet processing is serialized for one playback path.
- Added P25 DFSI receive decoding for complete LDU1/LDU2 packets: the nine IMBE codewords are reconstructed from records `0x62–0x6A` and `0x6B–0x73`, selected by talkgroup, and sent through the software-vocoder playback boundary. Key management/decryption is explicitly deferred so encrypted traffic is not silently treated as clear audio.
- Re-ran the supplied live FNE probe after attaching the traffic boundary; the system reached `Connected` in five seconds and exited successfully after clean shutdown.
- Retried the supplied live FNE codeplug after macOS Local Network permission was granted. The endpoint `10.10.10.55:62031` exchanged traffic, completed login/authentication, reached `Connected`, and shut down cleanly. The probe now preserves the observed-connected result after shutdown and exits successfully; diagnostic packet tracing is opt-in and sanitized.
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
| `e752a65` | Bounded live-FNE probe, testing documentation, and clean socket shutdown handling. |
| `83c727f` | Record the first bounded live-FNE probe result. |
| `321d059` | Record the pre-CoreAudio rebuild verification baseline. |
| `6223429` | Apple Silicon CoreAudio/AudioUnit backend, rate conversion, and stream probe. |
| `f1b3944` | Opt-in FNE diagnostics, stable connected-state reporting, and live-probe success tracking. |
| `f31e3f3` | Explicit Avalonia FNE connect/disconnect commands and lifecycle status display. |
| `4cff086` | Windows NAudio/WinMM audio backend and runtime backend factory. |
| `b0ab7f8` | Platform-neutral DMR/P25/NXDN/analog FNE traffic event boundary. |
| `e9d0646` | Streaming PCM/vocoder frame encoder and decoder pipeline. |
| `caca017` | Manual PTT source lifecycle and transition tests. |
| `4420bd0` | Reproducible Apple Silicon/Windows desktop publishing script and guide. |
| `b99390c` | Record publishing and manual-PTT milestones. |
| `e030bc5` | Record traffic-pipeline verification. |
| `548e0a0` | Record the platform-neutral audio boundary milestone. |
| `fe3c246` | Record the Avalonia shell milestone. |
| `9d18b95` | Record the FNE client milestone. |
| `4711b33` | Record the full-solution verification baseline. |
| `730a165` | Record CoreAudio and live-FNE milestones. |
| `9b46790` | DMR RX packet extraction, software-vocoder decode, playback session, and focused media tests. |
| `c13ecb8` | DMR receive channel selection and serialized playback routing. |
| `46443d3` | P25 DFSI LDU reconstruction, IMBE extraction, and clear receive playback tests. |

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
| PCM rate-converter tests | 6 passed on Apple Silicon |
| Native macOS audio shim | Passed CMake arm64 build; device enumeration passed for input/output devices |
| macOS audio stream probe | Passed default input/output stream test; captured 16,043 target-rate PCM samples over two seconds |
| Rebuild solution after audio boundary | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` (14 legacy FNE warnings) |
| Avalonia desktop shell | Built cleanly; launch check remained running as expected until the test process was interrupted |
| FNE connection service tests | 5 passed without opening a network connection |
| Rebuild solution after FNE client | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` (14 legacy FNE warnings) |
| Live FNE probe | Supplied private testing codeplug validated; 1 system reached `WaitingForLogin`, no `Connected` state in 10 seconds; clean shutdown, expected nonzero result |
| Live FNE probe after Local Network permission | Passed: 1 system exchanged traffic with `10.10.10.55:62031`, reached `Connected` within 10 seconds, shut down cleanly, and returned exit code 0 |
| Live FNE probe after traffic boundary | Passed: 1 system reached `Connected` within five seconds with traffic subscriptions attached, shut down cleanly, and returned exit code 0 |
| Avalonia shell startup | Passed: launched with `configs/codeplug.example.yml` and remained running until intentionally interrupted |
| Windows audio runtime | Not run on Windows hardware; compile verification passed on macOS |
| Desktop publish script | Passed framework-dependent `osx-arm64` and `win-x64` publishes; macOS output includes `libdvmaudio.dylib` |
| Final solution test run before media slice | 26 passed: Core 3, FNE 4, Vocoder 6, Audio 8, FNE client 5 (native vocoder included) |
| Media test project | 8 passed: DMR packet/decode/routing coverage plus P25 LDU1/LDU2 extraction and nine-frame playback |
| Final solution test run after media slice | 29 passed: Core 3, FNE 4, Vocoder 6, Audio 8, FNE client 5, Media 3 (native vocoder included) |
| Final solution build after media slice | Passed all 16 solution projects with `/m:1`; 0 warnings and 0 errors |
| Bootstrap example validation | Passed: 1 system and 3 zones loaded from `configs/codeplug.example.yml` |
| Rebuild solution | Passed with `dotnet build src/DvmConsole.Rebuild.sln --no-restore /m:1` |
| Bootstrap config validation | Passed with `configs/codeplug.example.yml` |
| Live testing config | Present locally and ignored by Git |
| Legacy application build on macOS | Not attempted; WPF is Windows-only |
