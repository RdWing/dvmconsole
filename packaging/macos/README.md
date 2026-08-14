# macOS .app Packaging (Avalonia Shell) — First Slice

<!-- SPDX-License-Identifier: AGPL-3.0-only -->

This directory contains the first, deliberately **bounded** slice of macOS
packaging for the Avalonia shell (`DvmConsole.Avalonia`): it produces an
**unsigned** `.app` bundle from a `dotnet publish` output directory.

| File | Purpose |
| --- | --- |
| `build-app.sh` | Assembles `<name>.app` (Contents/MacOS, Contents/Resources, Contents/Frameworks) from a publish directory. Unsigned, no notarization, no code signing of any kind. |
| `publish-app.sh` | Cleans the selected RID's generated Release outputs, publishes the Avalonia shell, assembles the bundle, and verifies the bundled `fnecore.dll` and `DvmConsole.Avalonia.dll` match the publish output. |
| `build-vocoder.sh` | Reproducible macOS gate: obtains the pinned dvmvocoder commit (`dvmvocoder.lock`) and builds unsigned `libvocoder.dylib` for arm64 + x86_64. |
| `dvmvocoder.lock` | Pin file: upstream URL + exact commit for `build-vocoder.sh`. |
| `../` + `../../DvmConsole.Avalonia/Platforms/macOS/Info.plist` | Bundle metadata template with `@DVM_*@` substitution tokens (see below). |
| `README.md` | This file. |

**Out of scope for this slice (do not expect them here):** code signing,
notarization/staple, and `dmg`/`zip` distribution. `build-app.sh` never signs,
notarizes, builds, or downloads the vocoder; the native library is produced by
the separate pinned gate `build-vocoder.sh` (see
[Build the native vocoder](#build-the-native-vocoder-libvocoderdylib)) and is
only copied into `Contents/Frameworks` when the caller supplies it — see
[libvocoder.dylib in the bundle](#libvocoderdylib-in-the-bundle).

## Prerequisites

- A macOS machine (Intel or Apple Silicon) — the script itself is portable
  bash, but `dotnet publish -r osx-*` must run on macOS to produce macOS
  binaries.
- .NET 8 SDK.
- A macOS machine with the Xcode Command Line Tools (clang) for the native
  vocoder build, plus `git`.
- CMake ≥ 3.16 for `build-vocoder.sh` (no Homebrew required — a user-local
  Python wheel works; see [Build the native vocoder](#build-the-native-vocoder-libvocoderdylib)).
- The checked-in `DvmConsole.Avalonia/Assets/AppIcon.icns` for the bundle icon
  (derived from the existing `AppIcon.ico`; see [Icon handling](#icon-handling)).

## Publish

Use the wrapper after changing the parent repository or the fnecore
submodule. It removes only generated Release output for the selected RID,
publishes from the exact parent/fnecore gitlink, assembles the app, and writes
an ignored `Contents/Resources/build-manifest.txt` containing the source SHAs
and assembly hashes:

```sh
# Apple Silicon (default; output under ignored artifacts/)
packaging/macos/publish-app.sh -r osx-arm64 -o artifacts/macos/osx-arm64/DvmConsole.app

# Intel/Rosetta
packaging/macos/publish-app.sh -r osx-x64 -o artifacts/macos/osx-x64/DvmConsole.app
```

The wrapper refuses tracked parent changes, a dirty fnecore checkout, or a
parent gitlink that does not match the checked-out fnecore `HEAD`. This is
intentional: a successful build must identify the source tree that produced
the bundle. Do not rely on a pre-existing RID-specific `publish/` directory
after updating the submodule.

For a framework-dependent development bundle, add
`--framework-dependent`. Pass `-v artifacts/vocoder/osx-arm64/libvocoder.dylib`
when the native vocoder has been built.

The lower-level `dotnet publish` plus `build-app.sh` commands remain useful
for packaging experiments, but they do not perform the freshness checks above.

## Assemble the .app

```sh
packaging/macos/build-app.sh \
  -p DvmConsole.Avalonia/bin/Release/net8.0/osx-arm64/publish \
  -o dist/DvmConsole.app
```

On success the script prints the absolute path of the finished bundle and a
reminder that it is unsigned.

### Options

| Option | Meaning |
| --- | --- |
| `-p DIR` | **Required.** Publish output directory. |
| `-o PATH` | **Required.** Output `.app` path (an existing one is removed and rebuilt). |
| `-e NAME` | Main executable name inside the publish dir. Default `DvmConsole.Avalonia` (must match `CFBundleExecutable`). |
| `-i ICNS` | **Required-icon mode:** install this `AppIcon.icns`. Fails if the file is absent. |
| `-v DYLIB` | Copy a caller-supplied `libvocoder.dylib` into `Contents/Frameworks`. Also read from `VOCODER_DYLIB`. Fails if the file is absent. |
| `-l PLIST` | Info.plist template override (default: the repo one under `DvmConsole.Avalonia/Platforms/macOS/`). |
| `-n` | Dry-run: validates inputs, prints the full plan, writes nothing. Exit 0 means the real run would succeed. |
| `-h` | Help. |

## Build the native vocoder (libvocoder.dylib)

The native vocoder is **not** vendored in this repository, and `build-app.sh`
never builds, fetches, or invents it. `build-vocoder.sh` is a reproducible gate
that obtains **exactly one pinned upstream commit** and produces unsigned
`libvocoder.dylib` artifacts for both macOS architectures:

- `dvmvocoder.lock` — pin file with `DVMVOCODER_URL` (upstream Git URL) and
  `DVMVOCODER_COMMIT` (exact 40-hex commit). The gate refuses anything else.
- `build-vocoder.sh` — clones/fetches the pinned commit, configures and builds
  it with CMake (Release, `arm64` + `x86_64`, macOS deployment target 12.0 by
  default), validates every output, and never signs or notarizes.

Pinned upstream: [DVMProject/dvmvocoder](https://github.com/DVMProject/dvmvocoder)
at commit `80ec5b66c0cfeff7b3f9ea9e1a8249f2b3ac3767`. Note the upstream project
is GPL-2.0-only; building it here is for local/offline packaging, and any
distribution of the resulting binaries must respect its license.

### Build

```sh
packaging/macos/build-vocoder.sh
```

Outputs (both Mach-O dylibs with install name `@rpath/libvocoder.dylib`):

```text
artifacts/vocoder/osx-arm64/libvocoder.dylib   # Apple Silicon
artifacts/vocoder/osx-x64/libvocoder.dylib     # Intel
```

The default out-root `artifacts/vocoder` is **outside Git** (`artifacts/` is
ignored; `dist/` is not, so do not point `--out-root` at `dist/`). Only the
lock file, the script, and this README are tracked — generated dylibs are
never committed.

### Options

| Option / env | Meaning |
| --- | --- |
| `--source-dir DIR` | Offline/review mode: use an existing checkout. Must be a clean Git work tree whose `HEAD` is exactly the pinned commit, otherwise the gate fails. Never mutated. |
| `--out-root DIR` / `DVM_VOCODER_OUT_ROOT` | Output root (default `artifacts/vocoder`). |
| `--cmake PATH` / `DVM_CMAKE` | CMake executable (default `cmake`). |
| `--deployment-target 12.0` / `DVM_MACOSX_DEPLOYMENT_TARGET` | `MACOSX_DEPLOYMENT_TARGET` (default `12.0`). |
| `--arches arm64,x86_64` / `DVM_VOCODER_ARCHES` | Architectures to build (default both). |
| `--dry-run` | Validate pin + inputs, print the plan, clone/build/write nothing. |

Each architecture runs
`cmake -S <src> -B <out>/build/<dir> -DCMAKE_BUILD_TYPE=Release
-DCMAKE_OSX_ARCHITECTURES=<arch> -DCMAKE_OSX_DEPLOYMENT_TARGET=<target>
-DCMAKE_OSX_SYSROOT=macosx` followed by `cmake --build ... --config Release`.

The gate then **fails rather than guesses**: each artifact must exist, be
reported by `file` as a Mach-O dynamically linked shared library with the exact
expected architecture, and export all eight MBE symbols
(`MBEEncoder_Create`/`Encode`/`EncodeBits`/`Delete`,
`MBEDecoder_Create`/`Decode`/`DecodeBits`/`Delete`, checked with `nm -gU`). A
wrong architecture is never accepted. No `codesign`, no notarization, and no
downloads beyond the pinned URL + commit from `dvmvocoder.lock`.

### Verification status

- **arm64**: verified end-to-end on Apple Silicon with this pinned commit —
  the managed vocoder-interop suite passes **83/83**.
- **x86_64**: the artifact builds and passes the same file/arch/export
  validation, but its native runtime was not exercised on the reference Mac
  (no Intel or Rosetta run); treat it as build-validated, not runtime-verified.

### Bundle it

```sh
packaging/macos/build-app.sh \
  -p DvmConsole.Avalonia/bin/Release/net8.0/osx-arm64/publish \
  -o dist/DvmConsole.app \
  -v artifacts/vocoder/osx-arm64/libvocoder.dylib
```

### Info.plist substitution tokens

The checked-in `Info.plist` is a template; `build-app.sh` substitutes the
following environment variables (with the documented defaults) into the copy
it installs at `Contents/Info.plist`:

| Token | Env var | Default |
| --- | --- | --- |
| `@DVM_BUNDLE_IDENTIFIER@` | `DVM_BUNDLE_IDENTIFIER` | `org.dvmproject.dvmconsole` |
| `@DVM_BUNDLE_SHORT_VERSION@` | `DVM_BUNDLE_SHORT_VERSION` | `0.1.0` |
| `@DVM_BUNDLE_VERSION@` | `DVM_BUNDLE_VERSION` | `1` |
| `@DVM_LS_MINIMUM_SYSTEM_VERSION@` | `DVM_LS_MINIMUM_SYSTEM_VERSION` | `12.0` |

The template stays valid XML (and even `plutil`-parseable) before
substitution, so it can be inspected with standard tools without a build.
The script verifies no `@DVM_*@` token survives into the installed plist and
validates the result with `plutil -lint` (macOS) or `xmllint`/Python
`plistlib` (fallbacks), and fails the build otherwise.

Example with an explicit version:

```sh
DVM_BUNDLE_SHORT_VERSION=0.2.0 DVM_BUNDLE_VERSION=3 \
  packaging/macos/build-app.sh -p .../publish -o dist/DvmConsole.app
```

### Icon handling

- `-i path/to/AppIcon.icns` → **required**: the file must exist or the script
  exits with an error (the bundle is not produced).
- no `-i`, but `AppIcon.icns` sits in the publish directory, its `Assets/`
  subdirectory, or the checked-in repo asset → copied automatically with a
  notice.
- none of those → the bundle builds with a warning; `CFBundleIconFile` still
  says `AppIcon`, so Finder shows a generic icon.

## libvocoder.dylib in the bundle

The Avalonia shell's vocoder support depends on the native **dvmvocoder**
library (`libvocoder.dylib` on macOS, logical name `libvocoder` per
`dvmconsole/VocoderInterop.cs` and `DvmConsole.Platform/Native` probing).
`build-app.sh` itself still does **not** build, fetch, or invent that library
— that is `build-vocoder.sh`'s job (see
[Build the native vocoder](#build-the-native-vocoder-libvocoderdylib)). Within
this packaging slice:

- Without `-v`/`VOCODER_DYLIB`, `Contents/Frameworks` is left empty and the
  app runs, but vocoder features that need the native library will report it
  unavailable.
- When a real dylib is supplied (typically one of the `build-vocoder.sh`
  outputs), it is copied into `Contents/Frameworks` as-is (no
  `install_name_tool` rewriting). Resolution of the packaged
  `Contents/Frameworks/libvocoder.dylib` is **now wired**: on macOS the
  Avalonia shell registers
  `DvmConsole.Platform.Native.MacBundleLibraryResolver` (via
  `NativeLibrary.SetDllImportResolver`), which maps the logical `libvocoder`
  name to `<bundle>/Contents/Frameworks/libvocoder.dylib` whenever the
  process base directory is `<bundle>/Contents/MacOS`, and returns
  `IntPtr.Zero` for every other case so normal runtime resolution is
  unchanged — un-packaged development runs therefore keep using the usual
  dyld search paths. The mapping never checks the filesystem; without
  `-v`/`VOCODER_DYLIB` the candidate path simply does not exist and the
  startup probe still reports the vocoder unavailable. Script behavior is
  unchanged.

## Unsigned status & running locally

Bundles produced here are **unsigned and unnotarized**. On first launch macOS
Gatekeeper will block the app with “cannot be opened because the developer
cannot be verified”. For local development:

```sh
xattr -dr com.apple.quarantine dist/DvmConsole.app   # clears quarantine
open dist/DvmConsole.app
```

(or right-click → Open → Open in Finder). The app may also be launched
directly from a terminal:

```sh
dist/DvmConsole.app/Contents/MacOS/DvmConsole.Avalonia
```

### Future: signing / notarization (not implemented)

Planned later slices, none of which this script performs:

1. Ad-hoc or Developer ID signing (`codesign --deep --force --options runtime
   --sign "Developer ID Application: ..." DvmConsole.app`) with hardened
   runtime.
2. Notarization (`xcrun notarytool submit` + `xcrun stapler staple`).
3. Optional `dmg` packaging.

Until then, treat every artifact from `build-app.sh` as development-only.
