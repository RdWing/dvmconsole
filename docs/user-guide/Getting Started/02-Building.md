# Building and packaging

Most users should download a release package. The steps below are for developers
who need to build or package DVM Console from source.

---

# Supported targets

- Apple Silicon macOS: `osx-arm64`
- Intel macOS: `osx-x64`
- 64-bit Windows: `win-x64`
- ARM64 Windows: `win-arm64`
- x86-64 Linux: `linux-x64`
- ARM64 Linux: `linux-arm64`

The tagged-release workflow includes all six targets in packaging, SBOM
generation, checksums, attestations, and verification of the uploaded files.
Both Windows targets use the same single-file package format. Linux releases use one AppImage per
architecture and retain the PipeWire/X11-or-XWayland support boundaries below.

The application version is defined centrally in `src/Directory.Build.props`.
See the current release notes for platform support and remaining limitations.

---

# Build requirements

Install these tools on the build host:

- Git
- .NET 10 SDK selected by `global.json`
- Rust 1.85 or newer
- CMake
- a C/C++ toolchain

On macOS, install Xcode Command Line Tools. On Windows, install Visual Studio
2022 Build Tools with the **Desktop development with C++** workload.

For a direct Linux build, also install the PipeWire development
headers, X11 RECORD runtime library, and `binutils`. On Debian or Ubuntu:

```sh
sudo apt-get install binutils libpipewire-0.3-dev libxtst6
```

Portable Linux packaging uses Docker and builds the native components inside a
Debian 12 baseline. This prevents a package made on a newer workstation from
silently requiring a newer GNU C Library than the release baseline supports.

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
dotnet restore dvmconsole.sln --locked-mode -p:Configuration=Release
dotnet build dvmconsole.sln --no-restore --configuration Release
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

The desktop smoke opens the real windows and exercises every distinct
Configuration Studio action route. File pickers, confirmations, managed saves,
and exports use an isolated temporary library with deterministic selections;
the supplied codeplug and the operator's managed library are not modified.

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
Use `win-arm64` for an ARM64 Windows machine; do not run that payload through
x64 emulation when validating native audio or PTT behavior.

For a deliberate non-Windows ARM64 cross-build, install `cargo-xwin` and set
`NativeVocoderCargoExtension=xwin` before running the shell publish script.
Native CI and ordinary Windows builds continue to use the installed MSVC
toolchain directly.

---

# Package Linux

For a portable package, run the Docker-backed build on a Linux host whose
architecture matches the runtime:

```sh
scripts/build-linux-portable.sh linux-x64 \
  /tmp/dvmconsole-linux DVMConsole-test-x86_64.AppImage
```

The command publishes to `/tmp/dvmconsole-linux/linux-x64`, verifies
that every packaged ELF stays at or below GLIBC 2.34, creates one executable
AppImage, and launches its Avalonia smoke windows under Xvfb. Use
`linux-arm64` and an `aarch64.AppImage` filename on an ARM64 Linux host.

For an operator installation, the AppImage is the entire application:

```sh
chmod +x DVMConsole-test-x86_64.AppImage
./DVMConsole-test-x86_64.AppImage
```

The AppImage is Linux's closest equivalent to handing over a Windows EXE or a
macOS app bundle. It keeps DVM Console's managed files, native libraries, icon,
desktop entry, notices, and .NET runtime together behind one executable file.
Do not extract or rearrange its internal files.

A direct host build remains useful during development:

```sh
scripts/publish-desktop.sh linux-x64 /tmp/dvmconsole-linux-x64
scripts/verify-publish.sh linux-x64 /tmp/dvmconsole-linux-x64
```

Cross-compiling the native PipeWire shim requires a CMake toolchain file
supplied through `DVM_LINUX_CMAKE_TOOLCHAIN`; the toolchain must also provide
target PipeWire headers and libraries. A direct build on a newer distribution
can fail the GLIBC ceiling even when it runs on the build machine; use the
Docker-backed command for a portable package. Release AppImages are unsigned;
verify their GitHub attestation and `SHA256SUMS` entry before running them.

The AppImage is self-contained for .NET and the application-owned native
libraries, but it deliberately uses the host's desktop services and drivers:
ICU, PipeWire, X11 or XWayland, OpenGL, fonts, the XDG desktop portal, and the
X11 RECORD runtime library. Full desktop installations usually include most of
them. Minimal Debian and Ubuntu systems need the matching versioned ICU package
(`libicu70` on Ubuntu 22.04, `libicu72` on Debian 12, `libicu74` on Ubuntu 24.04
and Linux Mint 22.x, `libicu76` on Debian 13, or `libicu78` on Ubuntu 26.04),
plus `libpipewire-0.3-0`, `libxtst6`, and the ordinary X11/OpenGL/font libraries.
Fedora uses `libicu`, `pipewire-libs`, and `libXtst`.

The CI compatibility smoke runs the same AppImage on Debian 12 and 13, Ubuntu
22.04, 24.04, and 26.04, and Fedora 43. Native GitHub-hosted jobs build both
x86-64 and ARM64 packages. Linux Mint 22.x shares the Ubuntu 24.04 binary base,
and native Mint desktop acceptance covers audio enumeration, event-driven
hotplug, X11 PTT, packaged launch, and suspend/resume behavior.

Global keyboard PTT uses the X11 RECORD extension and does not grab or swallow
keys on X11. On Wayland, it requests one push-to-talk shortcut through the XDG
GlobalShortcuts portal; the desktop controls the permission prompt and may let
the operator choose or confirm the shortcut. Newer portal releases also require
the AppImage to be integrated with the desktop menu so its application identity
is available. If global capture is unavailable or declined, focused-window,
card, and serial PTT remain available.

Linux suspend and resume are observed through systemd-logind's
`PrepareForSleep` signal, independent of whether the desktop is GNOME, KDE,
Cinnamon, or another environment using logind. PipeWire device discovery now
lists physical capture and playback nodes with stable PipeWire serial IDs while
retaining explicit default-route choices. Hotplug refresh is performed whenever
the audio settings enumerate devices, while PipeWire registry and default-route
events trigger an immediate refresh. A low-rate poll remains as recovery for an
audio service restart or a missed desktop notification.

---

# Documentation

Documentation source files are under:

```
docs/user-guide
```

---

# Tagged releases

Pushing a version tag starts the macOS, Windows, and Linux test and packaging
matrix. Both macOS jobs smoke-test the packaged application bundle. The Windows
and Linux jobs run Avalonia headful smoke tests. Add version-matched release
notes before pushing the tag.

The workflow stages a draft release with four versioned ZIPs, two AppImages,
six SPDX JSON SBOMs, and `SHA256SUMS`. It creates GitHub artifact attestations,
downloads each staged asset, and verifies the hashes, title, notes, and
attestations before publishing. Source tests, package smoke tests, live FNE
trials, and hardware tests are separate evidence tiers. The workflow reports
only the checks it can run. Signing and notarization are reported when
available but are not required without maintainer-owned credentials.

---

# Release acceptance

Before handing a package to an operator:

1. Launch the extracted package on the target operating system.
2. Load a non-private test codeplug and connect to a test FNE.
3. Select the intended microphone and speaker under **Audio > Audio settings**.
4. Confirm receive audio, card PTT, global PTT, the permit tone, and TAR playback.
5. Send QCII and alert audio to at least two armed channels.
6. Close and reopen the application and confirm settings and channel positions are restored.

Cross-publishing does not replace testing the package on real macOS, Windows,
or Linux hardware. A final candidate still requires the live trials above on
each architecture being published.
