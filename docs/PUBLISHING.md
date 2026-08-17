# Desktop building and publishing

The Avalonia application supports Apple Silicon macOS (`osx-arm64`), Intel
macOS (`osx-x64`), and 64-bit Windows (`win-x64`). Release packages are
self-contained; end users do not need .NET or Rust installed.

## Build requirements

- Git with submodule support
- .NET 10 SDK selected by `global.json`
- Rust 1.85 or newer with the target matching the requested runtime
- CMake and a platform C/C++ toolchain for the macOS audio shim
- Xcode Command Line Tools on macOS, or Visual Studio Build Tools with the
  Desktop development with C++ workload on Windows

Clone recursively, then restore and build the Avalonia solution:

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git checkout avalonia_v2
git submodule update --init --recursive
dotnet restore src/DvmConsole.Rebuild.sln
dotnet build src/DvmConsole.Rebuild.sln
```

The build automatically compiles the locked native vocoder adapter. It is a
required component: there is no environment-variable override, allow-missing
mode, or UI-only package path. Native tests run as part of the normal solution
test suite.

## Versioning

`src/Directory.Build.props` is the single release-version source. Tags use
`v<VersionPrefix>`. The same informational version is used for assemblies,
packages, About, release naming, and the `DVMC_AV_<version>` FNE software
identifier; build metadata is removed only from the FNE identifier.

## macOS packages

Choose the runtime matching the destination Mac and publish it:

```sh
RID=osx-arm64 # use osx-x64 for Intel
PUBLISH_DIR="/tmp/dvmconsole-$RID"
scripts/publish-desktop.sh "$RID" "$PUBLISH_DIR"
scripts/verify-publish.sh "$RID" "$PUBLISH_DIR"
scripts/package-desktop.sh "$RID" "$PUBLISH_DIR" "/tmp/dvmconsole-$RID.zip"
```

The scripts build the native components for the selected architecture and
enforce the macOS 14 deployment floor. Packaging produces both
`/tmp/DVMConsole.app` and the unsigned ZIP. Test the app with:

```sh
open /tmp/DVMConsole.app
```

Do not rename or move files inside the app bundle. After moving an official
unsigned release to Applications, quarantine may be removed with:

```sh
xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
```

Do not run that command for an app obtained from another source.

## Windows x64 package

Run the PowerShell publisher and package the verified output:

```powershell
.\scripts\publish-desktop.ps1 `
  -Runtime win-x64 `
  -OutputDirectory C:\Temp\dvmconsole-win-x64

.\scripts\package-desktop.ps1 `
  -PublishDirectory C:\Temp\dvmconsole-win-x64 `
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

The delivery application is `DvmConsole.exe`. It is a self-contained
single-file executable, including the required native vocoder; a vocoder DLL
beside the EXE is a packaging failure. Windows audio does not include the
macOS audio shim.

## Release acceptance

Before handing an archive to end users:

1. Start the extracted `DVMConsole.app` or `DvmConsole.exe` on matching
   hardware.
2. Open a non-private test codeplug and connect to a test FNE.
3. Select input and output devices and confirm receive audio.
4. Confirm card PTT, global PTT, talk-permit tone, and recording playback.
5. Exercise direct PTT, tones/pages, and patch forwarding for each supported
   digital mode, including configured encrypted channels.
6. Close and reopen the application and confirm settings and widget positions
   are restored.

Apple Silicon, Intel macOS, and Windows packages each require hardware
acceptance. A successful cross-publish does not replace running the package on
its destination platform.

## Package contents and optional media

The publishers do not copy codeplugs, alias files, encryption-key files, or
user settings. Markdown documentation is read from GitHub and is not embedded
in release archives. WAV and MP3 playback require no external process; FFmpeg
remains optional for additional media formats through `DVM_FFMPEG`.

## CI and tagged releases

The `Avalonia rebuild` workflow installs the pinned Rust toolchain, runs
formatting, lint, native tests, and the full .NET test matrix on all three
targets, then publishes and verifies unsigned packages. It checks native
architecture, the macOS deployment floor, Windows single-file layout, notices,
and exclusion of private test material.

A tag matching the central source version runs the same matrix and publishes
the three packages after all jobs pass. Do not push a release tag until release
notes and hardware acceptance are approved.
