# Desktop building and publishing

The Avalonia application supports Apple Silicon macOS (`osx-arm64`), Intel
macOS (`osx-x64`), and 64-bit Windows (`win-x64`). The publishing scripts
produce self-contained applications: an end user does not need the .NET SDK
or the .NET Desktop Runtime installed.

These instructions have separate build-machine and end-user sections. A build
machine needs the SDK and native toolchain; an end user needs only the
extracted `DVMConsole.app` on macOS or the extracted Windows application
folder.

## Versioning and commit history

The first public Avalonia release is `0.1.0`. Future versions follow Semantic
Versioning. Increment
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

All targets require Git, the .NET 10 SDK selected by `global.json`, CMake,
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
.NET 10 SDK and Visual Studio 2022 Build Tools with the **Desktop development
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

The native vocoder integration cases cannot run without that environment
variable; the managed projects can still be restored and built without it.

## End User Packages

The publishing scripts produce self-contained applications. The .NET SDK and
.NET Desktop Runtime are not required on the destination computer.

### macOS

1. Choose the runtime identifier matching the destination Mac and provide a
`libvocoder.dylib` built for the same architecture. Use `osx-arm64` for Apple
Silicon or `osx-x64` for Intel. The shell publisher cross-builds the matching
CoreAudio shim and copies both native libraries into one directory:

```sh
RID=osx-arm64 # use osx-x64 for Intel
PUBLISH_DIR="/tmp/dvmconsole-$RID"
DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib \
  scripts/publish-desktop.sh "$RID" "$PUBLISH_DIR"

scripts/verify-publish.sh "$RID" "$PUBLISH_DIR"
```

The supported deployment floor is macOS 14 for both architectures. The
publisher passes that target to the CoreAudio shim build. Build the separately
supplied vocoder with `-DCMAKE_OSX_DEPLOYMENT_TARGET=14.0` as well; CI does this
for both Mac targets, and the artifact verifier rejects a different native
deployment target.

2. To create an unsigned application bundle and ZIP handoff, package the
verified publish directory:

```sh
scripts/package-desktop.sh "$RID" "$PUBLISH_DIR" \
  "/tmp/dvmconsole-$RID.zip"
```

3. This leaves both `/tmp/DVMConsole.app` and the ZIP. The `.app` is the same
bundle placed in the ZIP; it is no longer deleted as temporary packaging
output. Test it with:

```sh
open /tmp/DVMConsole.app
```

Do not rename or move files inside `DVMConsole.app`. The managed assemblies and
native libraries are loaded relative to the bundled executable. The in-app
viewer reads current documentation from GitHub. Alert 1 through Alert 3 are
generated in memory and do not require external WAV assets.

4. The bundle launches the bundled `DVM Console` executable and does not
depend on `dotnet` being installed. The old `dotnet DvmConsole.Desktop.dll`
command is only a development launch path.

5. The package is unsigned. A downloaded unsigned bundle may be reported as
damaged because its quarantine attribute cannot be cleared by a signature.
After moving an official release to Applications, remove quarantine with:

```sh
xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
```

Do not use this command on an app obtained from any other source. macOS may
request local-network access on the first FNE connection and microphone
permission the first time PTT is used. Grant Accessibility or Input Monitoring
access if global Space/F-key PTT is required.

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

2. The output contains a self-contained single-file
`DvmConsole.Desktop.exe` and the native `libvocoder.dll`. It can be tested
directly:

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

The PowerShell publisher and packager verify that the application and native
vocoder are Windows x64 PE files. The managed runtime and dependencies are
inside the application EXE, leaving only the separate native vocoder beside it.

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
test. Apple Silicon and Intel packages must also be exercised on their matching
hardware before release. A successful cross-publish is not a substitute for
running the package on its destination architecture and operating system.

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

## Documentation delivery

Markdown under `dvmconsole/Docs` remains the documentation source in Git. It is
not copied into publish or release output. The in-app viewer retrieves those
pages from the `avalonia_v2` branch on GitHub, so it needs an internet
connection and does not preserve a stale package-time copy.

## CI validation and tagged releases

The `Avalonia rebuild` workflow runs the complete solution test suite on Apple
Silicon macOS, Intel macOS, and Windows x64. Each runner checks out a pinned revision of
`DVMProject/dvmvocoder`, builds the native library for the target architecture,
runs its encode/decode integration tests, and includes that library in the
package. The workflow publishes self-contained apphosts, verifies apphost and
native-library architecture, checks for excluded test material, and uploads
unsigned packages for seven days.

Pushing a tag that matches the source version, such as `v0.1.0`, runs the same
three-target test and packaging matrix. If all jobs pass, the workflow creates
a GitHub release and attaches the versioned Apple Silicon macOS, Intel macOS,
and Windows ZIP files. A
matching `docs/releases/<tag>.md` file supplies curated notes; otherwise GitHub
generates notes from the commit history. Do not push a release tag until the
release notes and hardware acceptance checks have been approved.

FFmpeg remains optional. If it is unavailable on a runner, the additional OGG
decoder test is reported as skipped; WAV and MP3 support and the complete radio
package are still built and tested.

Intel macOS and Windows radio, microphone, speaker, and global-hotkey hardware
validation remain operator test steps.
