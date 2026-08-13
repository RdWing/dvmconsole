# Building

This page explains how to build the Digital Voice Modem Desktop Dispatch
Console from source. The original Windows client is WPF; the macOS desktop
client is the Avalonia shell in `DvmConsole.Avalonia`.

Most developers should use Visual Studio with the .NET desktop workload.

---

# Requirements

## Visual Studio

Install Visual Studio 2022 or newer with:

```
.NET Desktop Development
```

## Git

Git is required to clone the repository and submodules.

## Windows

The WPF console is intended to build and run on Windows.

## macOS Avalonia prerequisites

Install the .NET 8 SDK, Xcode Command Line Tools and Git. The native vocoder
build additionally needs CMake. macOS packaging supports Apple Silicon
(`osx-arm64`) and Intel/Rosetta (`osx-x64`).

---

# Clone the Repository

Use `--recurse-submodules` so required submodules are downloaded.

```bash
git clone --recurse-submodules https://github.com/DVMProject/dvmconsole.git
cd dvmconsole
```

If the repository was already cloned without submodules, run:

```bash
git submodule update --init --recursive
```

---

# Open the Solution

Open:

```
dvmconsole.sln
```

from Visual Studio.

You can open it by double-clicking the solution file or by using:

```
File > Open > Project/Solution
```

---

# Build

Select the desired platform, usually `x64` or `x86`, then build:

```
Build > Build Solution
```

or press:

```
Ctrl + Shift + B
```

The app targets .NET for Windows and includes WPF UI resources, audio assets, and markdown documentation files.

---

# Build the macOS Avalonia client

Run these commands from the repository root on macOS:

```bash
dotnet publish DvmConsole.Avalonia/DvmConsole.Avalonia.csproj \
  -c Release -r osx-arm64 --self-contained

packaging/macos/build-app.sh \
  -p DvmConsole.Avalonia/bin/Release/net8.0/osx-arm64/publish \
  -o dist/DvmConsole.app
```

For Intel or Rosetta, replace `osx-arm64` with `osx-x64`. To include the native
vocoder, build it first and pass the resulting dylib:

```bash
packaging/macos/build-vocoder.sh
packaging/macos/build-app.sh \
  -p DvmConsole.Avalonia/bin/Release/net8.0/osx-arm64/publish \
  -o dist/DvmConsole.app \
  -v artifacts/vocoder/osx-arm64/libvocoder.dylib
```

`build-app.sh` creates an unsigned, unnotarized development `.app`. It does
not sign, notarize or download the vocoder. See `packaging/macos/README.md`
for the full option and verification reference.

## macOS permissions and runtime paths

At runtime, grant **Microphone** permission when macOS asks for audio capture.
The global PTT hotkey requires **Accessibility** and **Input Monitoring** in
System Settings > Privacy & Security. The app reports permission-required and
does not bypass TCC. A clean-account permission transition still requires
macOS host validation; Linux builds cannot prove it.

The default application-data root for `UserSettings.json` is:

```text
~/Library/Application Support/DVMProject/dvmconsole/
```

Startup first checks `<application-data>/codeplug.yml`. If it is absent, the
current shell falls back to `Environment.CurrentDirectory/configs/codeplug.yml`;
use File → Open Codeplug for an explicit packaged-app path. System alias files
are loaded from the configured `Codeplug.System.AliasPath` (default `./alias.yml`)
and are not automatically remapped into Application Support. Debug Logs are a
bounded in-memory `LogBuffer`; Save writes a user-selected snapshot. TAR
recordings default below the user's Documents folder. A fully self-contained
packaged deployment must therefore provide explicit codeplug/alias paths rather
than assuming the repository checkout is present.

The app's Help menu opens the published documentation in the host browser;
the bundle does not embed a Markdown renderer. The About dialog reports the
release/hash, runtime/architecture and native-vocoder readiness.

---

# Run

Run from Visual Studio with:

```
Debug > Start Debugging
```

or press:

```
F5
```

The compiled app is written under the project `bin` directory for the selected platform and configuration.

Example:

```
dvmconsole\bin\x64\Debug\net8.0-windows7.0\
```

---

# Documentation Files

The WPF documentation viewer reads markdown files from:

```
dvmconsole\Docs
```

The Avalonia packaged shell uses the external documentation opener, so it does
not require the repository's Markdown files at runtime. Keep new documentation
tracked in this tree and update the macOS feature matrix when a parity gate
changes the operator surface.

---

# Troubleshooting

## Submodules are missing

Run:

```bash
git submodule update --init --recursive
```

## Build fails due to missing Windows desktop support

Verify that Visual Studio has the `.NET Desktop Development` workload installed.

## Build succeeds but docs are missing in the app

Verify that the markdown files are included as content in `dvmconsole.csproj` and copied to the output directory.
