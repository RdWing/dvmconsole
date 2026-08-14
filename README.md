# Digital Voice Modem Desktop Dispatch Console

The Digital Voice Modem Desktop Dispatch Console ("DDC") is a WPF desktop application that operates similarly to a traditional dispatch console, allowing DVM users to monitor multiple talkgroups on a DVM FNE from a single application.

![Dark Mode Console](./repo/Screenshot-3.png)

## Building

The original DDC is a WPF application built from the Visual Studio solution.
The macOS desktop port is the Avalonia shell in `DvmConsole.Avalonia` and is
built with the .NET 8 SDK. See
[`dvmconsole/Docs/Getting Started/02-Building.md`](dvmconsole/Docs/Getting%20Started/02-Building.md)
for complete Windows and macOS instructions.

### Dependencies

- dvmvocoder (libvocoder); https://github.com/DVMProject/dvmvocoder

### Build Instructions

1. Clone the repository. `git clone --recurse-submodules https://github.com/DVMProject/dvmconsole.git`
2. Switch into the "dvmconsole" folder.
3. Open the "dvmconsole.sln" with Visual Studio.
4. Select "x86" as the CPU type.
5. Compile.

### macOS Avalonia build and bundle

From a macOS checkout with the .NET 8 SDK, use the freshness-checked wrapper:

```sh
packaging/macos/publish-app.sh \
  -r osx-arm64 \
  -o artifacts/macos/osx-arm64/DvmConsole.app
open artifacts/macos/osx-arm64/DvmConsole.app
```

Use `-r osx-x64` for an Intel/Rosetta bundle. The wrapper cleans only
RID-specific generated output, records the parent/fnecore SHAs in the bundle
manifest, and verifies that the bundled managed assemblies match the exact
publish output. The lower-level `dotnet publish` plus
`packaging/macos/build-app.sh` sequence is available for packaging experiments
but does not perform those freshness checks.

The bundle is unsigned and unnotarized unless the later signing pipeline is
run on the user's Mac. The native `libvocoder.dylib` is optional for
assembling a development bundle but required for vocoder-backed voice
operation; build it with `packaging/macos/build-vocoder.sh` and pass `-v` to
`publish-app.sh`.

On first launch, macOS may require Microphone permission for audio capture and
Accessibility plus Input Monitoring permission for the global PTT hotkey.
Grant those permissions in System Settings > Privacy & Security; the app does
not bypass TCC or silently prompt for Accessibility/Input Monitoring.

The packaged app uses the macOS application-data path under
`~/Library/Application Support/DVMProject/dvmconsole/` for `UserSettings.json`
and the first startup candidate for `codeplug.yml`. If that candidate is
absent, startup falls back to `Environment.CurrentDirectory/configs/codeplug.yml`;
use File → Open Codeplug to select another file. System alias files are read
from the configured `Codeplug.System.AliasPath` (whose current default is
`./alias.yml`) and are not silently relocated. Debug Logs show a bounded recent
in-memory buffer, while the full redacted diagnostic stream is appended to
`DvmConsole.log` in that same application-data directory. The file records
both application (`[APP]`) and fnecore (`[FNE]`) events, including managed
exception details when the runtime reports them. For a bounded FNE diagnostic
run, set `DVMCONSOLE_FNE_RAW_PACKET_TRACE=1` to include packet hex dumps and
`DVMCONSOLE_FNE_TRAFFIC_LOGGING=0` to disable decoded traffic summaries;
these are enabled by default for diagnostics, while raw tracing remains
off by default because it is high-volume.
`DVMCONSOLE_FNE_LOG_LEVEL` controls fnecore's inclusive threshold and defaults
to `FATAL`, which retains all fnecore levels. See
`dvmconsole/Docs/Porting/macOS Feature Matrix.md` for implemented areas,
host-dependent verification and known limitations.

### Avalonia shell controls

The Avalonia/macOS shell keeps FNE connection status and call history in the
main dashboard. Call History has a session-only filter for channel, system,
alias, source RID, and destination TGID. Settings → Shell Controls provides
active-zone select/clear-all, widget visibility, user background selection,
confirmation-gated settings reset, persisted layout reset/fit/lock, and
always-on-top.

The current Avalonia dashboard uses a managed channel grid rather than the WPF
draggable Canvas. Layout actions therefore update the persisted layout contract
and shell/window state; they are not evidence of a per-card drag editor. Native
file-picker, Aqua window, TCC, CoreAudio, vocoder, FNE/radio, and browser-launch
behavior still require separate macOS host evidence.

Please note that while x64 CPU types are supported, the dvmvocoder library must be compiled separately for that architecture.

## dvmconsole Configuration

1. **Create/Edit `codeplug.yml`**  
   An example codeplug is provided in the `configs` directory. Configure system parameters, network settings, and talkgroups as needed.  
   The file paths for both `keys.clear` and `alias.yml` must be defined within `codeplug.yml`.

2. **Configure Encryption Keys (`keys.clear`)**  
   If your system's talkgroups use encryption, define your key entries in the `keys.clear` file.  
   Each key entry should match the Key ID referenced in your codeplug.

3. **Configure RID Aliases (`alias.yml`)**  
   To display friendly names instead of raw RIDs, populate `alias.yml` with your Radio ID to alias mappings.  
   This allows the console to show readable identifiers for subscriber units.

4. Start `dvmconsole`.

5. Use **“Open Codeplug”** within the application to load your configuration.

## Project Notes

- The Desktop Dispatch Console does not support interfacing to base station or mobile radios. For a DVM-compatible console that supports base/mobile radio interfacing, see: https://github.com/W3AXL/RadioConsole2 and  https://github.com/W3AXL/rc2-dvm.

## License

This project is licensed under the AGPLv3 License – see the [LICENSE](LICENSE) file for details.

This software is intended for amateur and/or educational use. Any other use is at the user's discretion and risk. Commercial use is strongly discouraged.
