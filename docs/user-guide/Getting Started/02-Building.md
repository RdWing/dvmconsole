# Building and packaging

Most users should download a release package. The steps below are for developers
who need to build or package DVM Console from source.

---

# Supported targets

- Apple Silicon macOS: `osx-arm64`
- Intel macOS: `osx-x64`
- 64-bit Windows: `win-x64`

The application version is defined centrally in `src/Directory.Build.props`.

---

# Build requirements

Install these tools on either build platform:

- Git
- .NET 10 SDK selected by `global.json`
- Rust 1.85 or newer
- CMake
- a C/C++ toolchain

On macOS, install Xcode Command Line Tools. On Windows, install Visual Studio
2022 Build Tools with the **Desktop development with C++** workload.

---

# Clone and build

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

Run the complete solution test suite before packaging:

```sh
scripts/verify-format.sh
dotnet test dvmconsole.sln --no-restore --disable-build-servers \
  --configuration Release /m:1 /p:UseSharedCompilation=false
```

Validate a codeplug without opening the desktop application:

```sh
dotnet run --project src/DvmConsole.CodeplugValidator -- path/to/codeplug.yml
```

Developers can use `DvmConsole.FneProbe`, `DvmConsole.AudioProbe`, and
`DvmConsole.MediaProbe` for live network, hardware audio, and media checks.
Release packages do not include these tools as operator applications.

---

# Package for macOS

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

These commands create `DVMConsole.app` and a ZIP containing the bundle.

Do not move or rename files inside the application bundle. The managed
assemblies, native libraries, icon, license, and third-party notices are loaded
relative to the application executable.

The package is unsigned. After moving an archive from an RdWing GitHub Release
to Applications, remove its download quarantine before launching:

```sh
xattr -dr com.apple.quarantine "/Applications/DVMConsole.app"
```

Do not run this command on an app from another source. macOS may also request
local-network, microphone, Accessibility, or Input Monitoring permission for
FNE connections, transmit audio, and OS-global PTT.

---

# Package for Windows

From PowerShell:

```powershell
.\scripts\publish-desktop.ps1 `
  -Runtime win-x64 `
  -OutputDirectory C:\Temp\dvmconsole-win-x64

.\scripts\package-desktop.ps1 `
  -PublishDirectory C:\Temp\dvmconsole-win-x64 `
  -OutputArchive C:\Temp\dvmconsole-win-x64.zip
```

Extract the ZIP before launching `DvmConsole.exe`.

---

# Documentation

Documentation source files are under:

```
docs/user-guide
```

---

# Tagged releases

Pushing a version tag such as `v0.5.1` starts the macOS and Windows test and
packaging matrix. Both macOS jobs smoke-test the packaged application bundle.
The Windows job runs the Avalonia headful smoke test. Add version-matched release
notes before pushing the tag.

The workflow stages a draft release with three versioned ZIPs, three SPDX JSON
SBOMs, and `SHA256SUMS`. It creates GitHub artifact attestations, downloads each
staged asset, and verifies the hashes, title, notes, and attestations before
publishing. Source tests, package smoke tests, live FNE trials, and hardware
tests are separate evidence tiers. The workflow reports only the checks it can
run. Signing and notarization are reported when available but are not required
without maintainer-owned credentials.

---

# Release acceptance

Before handing a package to an operator:

1. Launch the extracted package on the target operating system.
2. Load a non-private test codeplug and connect to a test FNE.
3. Select the intended microphone and speaker under **Audio > Audio settings**.
4. Confirm receive audio, card PTT, global PTT, the permit tone, and TAR playback.
5. Send QCII and alert audio to at least two armed channels.
6. Close and reopen the application and confirm settings and channel positions are restored.

Cross-publishing does not replace testing the package on real macOS and Windows
hardware.
