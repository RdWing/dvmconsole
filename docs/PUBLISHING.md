# Desktop building and publishing

The Avalonia application supports Apple Silicon macOS (`osx-arm64`) and
64-bit Windows (`win-x64`). The publishing scripts produce self-contained
applications: an end user does not need the .NET SDK or the .NET Desktop
Runtime installed.

These instructions have separate build-machine and end-user sections. A build
machine needs the SDK and native toolchain; an end user needs only the
extracted `DVMConsole.app` on macOS or the extracted Windows application
folder.

## Versioning and commit history

The Avalonia rebuild is currently unreleased. Development packages identify
the application as `0.1.0-alpha.1`; they are test artifacts and are not a
stable `1.0.0` release. Future versions follow Semantic Versioning. Increment
the major version for incompatible operator or configuration changes, the
minor version for backward-compatible capability, and the patch version for
backward-compatible corrections. Use pre-release suffixes until a build is
ready for general use.

Commit subjects follow the Conventional Commits format. Use `feat:` for new
operator capability, `fix:` for corrections, `docs:` for documentation,
`test:` for test-only changes, `build:` for packaging or dependency work,
`ci:` for workflow changes, and `chore:` for maintenance. A breaking change
uses `!` after the type or a `BREAKING CHANGE:` footer.

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

Do not rename or move files inside `DVMConsole.app`. The managed assemblies,
native libraries, and documentation are loaded relative to the bundled
executable. Alert 1 through Alert 3 are generated in memory and do not require
external WAV assets.

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

The PowerShell publisher and packager verify that the application apphost and
native vocoder are Windows x64 PE files. Extract the entire ZIP before testing;
copying only the `.exe` does not produce a runnable installation.

## Release acceptance

Before handing an archive to end users, test the extracted package on the
matching operating system. Do not use the development output under `bin/` as a
release package.

1. Start the extracted application by double-clicking `DVMConsole.app` or
`DvmConsole.Desktop.exe`.
2. Open a non-private test codeplug and connect to a test FNE.
3. Select the intended input and output devices under **Audio > Audio
Settings**.
4. Confirm receive audio, card PTT, global PTT, the talk permit tone, and
recording playback through the selected output device.
5. Arm two resources for `PAGE` and send a QCII page, then arm two resources
for `ALERT` and send Alert 1, Alert 2, Alert 3, and a DTMF sequence.
6. Close and reopen the application and confirm settings and widget positions
are restored.

The application uses the same managed console, FNE, vocoder, and media logic
on macOS and Windows. Audio device access and global keyboard capture use
platform-specific backends, so both operating systems require this acceptance
test. A successful cross-publish on macOS is not a substitute for running the
Windows package on Windows hardware.

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

The `Avalonia rebuild` workflow runs the complete solution test suite on Apple
Silicon macOS and Windows x64. Each runner checks out a pinned revision of
`DVMProject/dvmvocoder`, builds the native library for the target architecture,
runs its encode/decode integration tests, and includes that library in the
package. The workflow publishes self-contained apphosts, verifies apphost and
native-library architecture, checks for excluded test material, and uploads
unsigned packages for seven days.

FFmpeg remains optional. If it is unavailable on a runner, the additional OGG
decoder test is reported as skipped; WAV and MP3 support and the complete radio
package are still built and tested.

Windows radio, microphone, speaker, and global-hotkey hardware validation
remains an operator test step.
