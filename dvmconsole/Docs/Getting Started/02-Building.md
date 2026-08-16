# Building and Packaging

This page explains how to build and package the Avalonia DVMConsole application.

Most users should download a release package instead of building from source. A complete package is self-contained and does not require the .NET runtime on the destination computer.

---

# Supported Targets

- Apple Silicon macOS: `osx-arm64`
- 64-bit Windows: `win-x64`

The current prerelease version is `0.1.0-alpha.1`. Development packages are test artifacts and are not a stable `1.0.0` release.

---

# Build Requirements

Both build platforms require:

- Git
- .NET 8 SDK selected by `global.json`
- CMake
- a C/C++ toolchain
- a matching native `dvmvocoder` library

On macOS, install Xcode Command Line Tools. On Windows, install Visual Studio 2022 Build Tools with the **Desktop development with C++** workload.

---

# Clone and Build

Clone the repository with its submodules:

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git checkout avalonia_v2
git submodule update --init --recursive
```

Restore and build the rebuild solution:

```sh
dotnet restore src/DvmConsole.Rebuild.sln
dotnet build src/DvmConsole.Rebuild.sln
```

---

# Native Vocoder

DMR and P25 voice require `dvmvocoder` from:

```
https://github.com/DVMProject/dvmvocoder
```

Build a library that matches the target platform:

- macOS: `libvocoder.dylib`
- Windows: `libvocoder.dll`

Set `DVMVOCODER_LIBRARY` to its full path while testing or publishing. The publisher refuses to create a normal package without the matching vocoder because that package would not provide working digital voice.

---

# Test

Run the complete solution tests before packaging:

```sh
DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib \
  dotnet test src/DvmConsole.Rebuild.sln --no-restore \
  /p:UseSharedCompilation=false
```

Use the Windows DLL path in PowerShell when testing on Windows.

---

# Package macOS

Publish and verify the Apple Silicon application:

```sh
DVMVOCODER_LIBRARY=/full/path/to/libvocoder.dylib \
  scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64

scripts/verify-publish.sh osx-arm64 /tmp/dvmconsole-osx-arm64
scripts/package-desktop.sh osx-arm64 \
  /tmp/dvmconsole-osx-arm64 /tmp/dvmconsole-osx-arm64.zip
```

This creates `DVMConsole.app` and a ZIP containing that application bundle.

Do not move or rename files inside the application bundle. The managed assemblies, native libraries, icon, and documentation are loaded relative to the bundled executable.

The development package is unsigned. If Gatekeeper blocks it, use Finder's **Open** action from the context menu. macOS may also request microphone, Accessibility, or Input Monitoring permission for transmit and OS-global PTT.

---

# Package Windows

From PowerShell:

```powershell
$env:DVMVOCODER_LIBRARY = "C:\full\path\to\libvocoder.dll"
.\scripts\publish-desktop.ps1 `
  -Runtime win-x64 `
  -OutputDirectory C:\Temp\dvmconsole-win-x64

.\scripts\package-desktop.ps1 `
  -PublishDirectory C:\Temp\dvmconsole-win-x64 `
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

Extract the complete ZIP before launching `DvmConsole.Desktop.exe`. Copying only the EXE does not produce a working application.

---

# Documentation Files

The in-app documentation viewer reads Markdown files from:

```
dvmconsole/Docs
```

The files are copied into build and publish output. The viewer reads the selected file each time it is opened or selected, so an updated Markdown file can be reviewed without rebuilding the documentation UI.

---

# Release Acceptance

Before handing a package to an operator:

1. Launch the extracted package on the target operating system.
2. Load a non-private test codeplug and connect to a test FNE.
3. Select the intended microphone and speaker under **Audio > Audio settings**.
4. Confirm receive audio, card PTT, global PTT, the permit tone, and TAR playback.
5. Send QCII and alert audio to at least two armed channels.
6. Close and reopen the application and confirm settings and channel positions are restored.

Cross-publishing is not a substitute for running the package on real macOS and Windows hardware.
