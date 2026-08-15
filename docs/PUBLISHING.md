# Desktop building and publishing

The Avalonia application supports Apple Silicon macOS (`osx-arm64`) and
64-bit Windows (`win-x64`). The publishing scripts produce self-contained
applications: an end user does not need the .NET SDK or the .NET Desktop
Runtime installed.

These instructions have separate build-machine and end-user sections. A build
machine needs the SDK and native toolchain; an end user needs only the
extracted `DVMConsole.app` on macOS or the extracted Windows application
folder.

## Dependencies

Both platforms require Git, the .NET 8 SDK selected by `global.json`, CMake,
and a C/C++ toolchain.

### Build Instructions

1. Clone the repository with the FNE submodule:

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git checkout avalonia_v2
git submodule update --init --recursive
dotnet restore src/DvmConsole.Rebuild.sln
dotnet build src/DvmConsole.Rebuild.sln
```

2. On macOS, install Xcode Command Line Tools and CMake. On Windows, install the
.NET 8 SDK and Visual Studio 2022 Build Tools with the **Desktop development
with C++** workload (or an equivalent CMake/MSVC toolchain).

### Native vocoder

DMR and P25 voice require the native `dvmvocoder` library. It is not committed
to this repository. Build it separately as described in [VOCODER.md](VOCODER.md),
then set `DVMVOCODER_LIBRARY` to its full path while publishing. The resulting
library architecture must match the runtime identifier. The publisher fails
when this library is missing, because a package without it is not a working
digital-voice console.

Please note that UI-only inspection artifacts must explicitly set
`DVM_ALLOW_MISSING_VOCODER=1` or pass `-AllowMissingVocoder`. Such an artifact
can open its UI but cannot encode or decode DMR/P25 voice.

After setting `DVMVOCODER_LIBRARY`, run the complete test suite with:

```sh
dotnet test src/DvmConsole.Rebuild.sln --no-restore
```

The two native vocoder integration cases cannot run without that environment
variable; the managed projects can still be restored and built without it.

## End User Packages

The publishing scripts produce self-contained applications. The .NET SDK and
.NET Desktop Runtime are not required on the destination computer.

### Apple Silicon macOS

1. From the repository root, publish the application with a matching
`libvocoder.dylib`. The shell publisher builds the CoreAudio shim and copies
both native libraries into one directory:

```sh
DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib \
  scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64

scripts/verify-publish.sh osx-arm64 /tmp/dvmconsole-osx-arm64
```

2. To create an unsigned application bundle and ZIP handoff, package the
verified publish directory:

```sh
scripts/package-desktop.sh osx-arm64 \
  /tmp/dvmconsole-osx-arm64 /tmp/dvmconsole-osx-arm64.zip
```

3. This leaves both `/tmp/DVMConsole.app` and the ZIP. The `.app` is the same
bundle placed in the ZIP; it is no longer deleted as temporary packaging
output. Test it with:

```sh
open /tmp/DVMConsole.app
```

4. The bundle launches the bundled `DvmConsole.Desktop` executable and does not
depend on `dotnet` being installed. The old `dotnet DvmConsole.Desktop.dll`
command is only a development launch path.

5. The package is unsigned. If Gatekeeper blocks a double-click, use Finder's
**Open** action from the context menu on the app. macOS may request microphone
permission the first time PTT is used. Grant Accessibility or Input Monitoring
access to the application if global Space/F-key PTT is required.

If the application closes without displaying an error, collect the local
`LastCrash.log` before restarting it. On macOS it is stored under
`~/Library/Application Support/DVMProject/dvmconsole/`; on Windows it is stored
under `%APPDATA%\DVMProject\dvmconsole\`. The file contains the most recent
unhandled managed exception and does not contain the codeplug or encryption
keys.

The desktop also attempts OS-global Space/F-key PTT on macOS and Windows. On
macOS, grant the packaged application Accessibility or Input Monitoring access
if global PTT is required. If permission is unavailable, PTT falls back to
events received while the DVM Console window is focused.

### Windows x64

1. Run the PowerShell publisher from a PowerShell terminal with a matching
`libvocoder.dll`:

```powershell
$env:DVMVOCODER_LIBRARY = "C:\full\path\to\libvocoder.dll"
.\scripts\publish-desktop.ps1 -Runtime win-x64 -OutputDirectory C:\Temp\dvmconsole-win-x64
```

2. The output contains the self-contained `DvmConsole.Desktop.exe` and can be
tested directly:

```powershell
Start-Process C:\Temp\dvmconsole-win-x64\DvmConsole.Desktop.exe
```

3. Windows audio uses NAudio and does not require the macOS audio shim. The
PowerShell script verifies the required managed files and refuses to package
the private `codeplug_testing.yml`. From Git Bash, the cross-platform shell
script and verifier can also publish Windows:

```sh
DVMVOCODER_LIBRARY=/c/full/path/to/libvocoder.dll \
  scripts/publish-desktop.sh win-x64 /c/Temp/dvmconsole-win-x64
scripts/verify-publish.sh win-x64 /c/Temp/dvmconsole-win-x64
```

4. The PowerShell packaging helper creates an unsigned Windows ZIP from the
verified publish directory:

```powershell
.\scripts\package-desktop.ps1 \
  -PublishDirectory C:\Temp\dvmconsole-win-x64 \
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

## Native libraries and optional media support

The publisher places `libvocoder.dylib` or `libvocoder.dll` beside the
application apphost. Do not set `DVMVOCODER_LIBRARY` on the end-user machine;
that variable is a build-time input and the packaged application finds the
copied library beside itself.

Web streams decode WAV and MP3 without an external process. For additional
formats, install a compatible FFmpeg executable on the target machine and set
`DVM_FFMPEG` to its full path. FFmpeg is intentionally not bundled.

Neither publishing script copies a codeplug, alias file, encryption key file,
or user settings. Keep those files outside distributable application folders.

## CI validation

The `Avalonia rebuild` workflow runs the complete managed solution test suite
on Apple Silicon macOS and Windows x64, publishes self-contained apphosts,
verifies native-library placement and test-material exclusion, and uploads
unsigned outputs for seven days. CI permits a UI-only artifact when no native
vocoder is provisioned; a release package intended for radio operation must
publish with the matching native vocoder library. Windows radio, microphone,
and speaker hardware validation remains an operator test step.
