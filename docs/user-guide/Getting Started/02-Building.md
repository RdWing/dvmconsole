# Building and Packaging

This page explains how to build and package DVM Console.

Most users should download a release package instead of building from source. A complete package is self-contained and does not require the .NET runtime on the destination computer.

---

# Supported Targets

- Apple Silicon macOS: `osx-arm64`
- Intel macOS: `osx-x64`
- 64-bit Windows: `win-x64`

The application version is defined centrally in `src/Directory.Build.props`.

---

# Build Requirements

Both build platforms require:

- Git
- .NET 10 SDK selected by `global.json`
- Rust 1.85 or newer
- CMake
- a C/C++ toolchain

On macOS, install Xcode Command Line Tools. On Windows, install Visual Studio 2022 Build Tools with the **Desktop development with C++** workload.

---

# Clone and Build

Clone the repository with its submodules:

```sh
git clone --recurse-submodules https://github.com/RdWing/dvmconsole.git
cd dvmconsole
git submodule update --init --recursive
```

Restore and build the solution:

```sh
dotnet restore dvmconsole.sln
dotnet build dvmconsole.sln
```

---

# Test

Run the complete solution tests before packaging:

```sh
dotnet test dvmconsole.sln --no-restore --disable-build-servers \
  --configuration Release /m:1 /p:UseSharedCompilation=false
```

---

# Package macOS

Publish and verify the Apple Silicon application:

```sh
scripts/publish-desktop.sh osx-arm64 /tmp/dvmconsole-osx-arm64

scripts/verify-publish.sh osx-arm64 /tmp/dvmconsole-osx-arm64
scripts/package-desktop.sh osx-arm64 \
  /tmp/dvmconsole-osx-arm64 /tmp/dvmconsole-osx-arm64.zip \
  /tmp/DVMConsole-osx-arm64.app
scripts/smoke-desktop-macos.sh \
  /tmp/DVMConsole-osx-arm64.app configs/codeplug.example.yml
```

Use `osx-x64` instead of `osx-arm64` when packaging for an Intel Mac.

This creates `DVMConsole.app` and a ZIP containing that application bundle.

Do not move or rename files inside the application bundle. The managed assemblies, native libraries, and icon are loaded relative to the bundled executable. The license and third-party notices are included in the package; user-guide documentation is read from GitHub and is not copied into the bundle.

The package is unsigned. After moving an official release to Applications,
remove its download quarantine before launching:

```sh
xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
```

Do not use this command for an app obtained from another source. macOS may also
request local-network, microphone, Accessibility, or Input Monitoring
permission for FNE connections, transmit, and OS-global PTT.

---

# Package Windows

From PowerShell:

```powershell
.\scripts\publish-desktop.ps1 `
  -Runtime win-x64 `
  -OutputDirectory C:\Temp\dvmconsole-win-x64

.\scripts\package-desktop.ps1 `
  -PublishDirectory C:\Temp\dvmconsole-win-x64 `
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

Extract the ZIP before launching the self-contained `DvmConsole.exe`.

---

# Documentation

The source Markdown files remain under:

```
docs/user-guide
```

The files are not copied into build or release output. The in-app viewer reads
the current pages from the release branch on GitHub, so updated
documentation is available without rebuilding the application. An internet
connection is required.

---

# Tagged Releases

Pushing a version tag such as `v0.3.7` starts the macOS and Windows test and packaging matrix. Both native macOS rows smoke the packaged application bundle, and the Windows row runs the Avalonia headful smoke test. The workflow publishes a GitHub release only after all three target jobs pass and attaches the three versioned ZIP files. Release notes must be supplied in `docs/releases/v<version>.md` before the tag is pushed.

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
