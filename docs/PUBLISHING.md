# Desktop building and publishing

The Avalonia application supports Apple Silicon macOS (`osx-arm64`) and
64-bit Windows (`win-x64`). Published outputs are framework-dependent, so the
target computer must have the .NET 8 Desktop Runtime installed.

## Prerequisites

Both platforms require Git, the .NET 8 SDK selected by `global.json`, CMake,
and a C/C++ toolchain. Clone with the FNE submodule:

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git checkout avalonia_v2
git submodule update --init --recursive
dotnet restore src/DvmConsole.Rebuild.sln
dotnet build src/DvmConsole.Rebuild.sln
```

On macOS, install Xcode Command Line Tools and CMake. On Windows, install the
.NET 8 SDK and Visual Studio 2022 Build Tools with the **Desktop development
with C++** workload (or an equivalent CMake/MSVC toolchain).

DMR and P25 voice require the native `dvmvocoder` library. It is not committed
to this repository. Build it separately as described in [VOCODER.md](VOCODER.md),
then set `DVMVOCODER_LIBRARY` to its full path while publishing. The resulting
library architecture must match the runtime identifier.

After setting `DVMVOCODER_LIBRARY`, run the complete test suite with:

```sh
dotnet test src/DvmConsole.Rebuild.sln --no-restore
```

The two native vocoder integration cases cannot run without that environment
variable; the managed projects can still be restored and built without it.

## Apple Silicon macOS

The shell publisher builds the CoreAudio shim, publishes the managed app, and
copies both native libraries into one directory:

```sh
DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib \
  scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64

scripts/verify-publish.sh osx-arm64 /tmp/dvmconsole-osx-arm64
dotnet /tmp/dvmconsole-osx-arm64/DvmConsole.Desktop.dll /full/path/to/codeplug.yml
```

To create an unsigned application bundle and ZIP handoff, package the verified
publish directory:

```sh
scripts/package-desktop.sh osx-arm64 \
  /tmp/dvmconsole-osx-arm64 /tmp/dvmconsole-osx-arm64.zip
```

The bundle launcher uses the `DVM_DOTNET` environment variable when set, or
the `dotnet` command on `PATH`. The bundle is intentionally unsigned; signing,
entitlements, and notarization remain release-distribution steps.

macOS may request microphone permission the first time PTT is used. The
publisher itself produces a flat directory; the packaging helper creates only
an unsigned `.app` bundle. Gatekeeper signing and Apple notarization remain
release-distribution steps.

## Windows x64

Run the PowerShell publisher from a PowerShell terminal:

```powershell
$env:DVMVOCODER_LIBRARY = "C:\full\path\to\libvocoder.dll"
.\scripts\publish-desktop.ps1 -Runtime win-x64 -OutputDirectory C:\Temp\dvmconsole-win-x64
dotnet C:\Temp\dvmconsole-win-x64\DvmConsole.Desktop.dll C:\full\path\to\codeplug.yml
```

Windows audio uses NAudio and does not require the macOS audio shim. The
PowerShell script verifies the required managed files and refuses to package
the private `codeplug_testing.yml`. From Git Bash, the cross-platform shell
script and verifier can also publish Windows:

```sh
DVMVOCODER_LIBRARY=/c/full/path/to/libvocoder.dll \
  scripts/publish-desktop.sh win-x64 /c/Temp/dvmconsole-win-x64
scripts/verify-publish.sh win-x64 /c/Temp/dvmconsole-win-x64
```

The PowerShell packaging helper creates an unsigned Windows ZIP from a
verified publish directory:

```powershell
.\scripts\package-desktop.ps1 \
  -PublishDirectory C:\Temp\dvmconsole-win-x64 \
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

## Native libraries and optional media support

When `DVMVOCODER_LIBRARY` is omitted, publishing succeeds with a warning and
the UI can still be inspected, but DMR/P25 voice encoding and decoding are not
usable. The runtime expects `libvocoder.dylib` on macOS or `libvocoder.dll` on
Windows beside `DvmConsole.Desktop.dll`.

Web streams decode WAV and MP3 without an external process. For additional
formats, install a compatible FFmpeg executable on the target machine and set
`DVM_FFMPEG` to its full path. FFmpeg is intentionally not bundled.

Neither publishing script copies a codeplug, alias file, encryption key file,
or user settings. Keep those files outside distributable application folders.

## CI validation

The `Avalonia rebuild` workflow runs the complete managed solution test suite
on Apple Silicon macOS and Windows x64, publishes both supported runtime
identifiers, verifies native-library placement and test-material exclusion,
and uploads unsigned outputs for seven days. Native vocoder integration tests
remain skipped in CI unless a separately provisioned `DVMVOCODER_LIBRARY` is
available; Windows radio, microphone, and speaker hardware validation remains
an operator test step.
