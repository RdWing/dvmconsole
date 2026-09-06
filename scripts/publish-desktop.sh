#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"
RID="${1:-osx-arm64}"
OUTPUT_DIR="${2:-$ROOT_DIR/artifacts/$RID}"
OUTPUT_WAS_SUPPLIED=$([[ $# -ge 2 ]] && printf true || printf false)
CONFIGURATION="${CONFIGURATION:-Release}"
MACOS_DEPLOYMENT_TARGET="$(/usr/bin/tr -d '[:space:]' < "$ROOT_DIR/packaging/macos/deployment-target.txt")"

validate_publish_target() {
    local requested="$1"
    local arguments=(
        --target "$requested"
        --directory
        --repository-root "$ROOT_DIR"
        --staging-root "$BUILD_ROOT"
    )
    if [[ "$OUTPUT_WAS_SUPPLIED" == false ]]; then
        arguments+=(--allow-repository-target)
    fi
    python3 "$ROOT_DIR/scripts/validate-package-target.py" "${arguments[@]}"
}

case "$RID" in
    osx-arm64)
        TARGET_PLATFORM="macos"
        MACOS_ARCHITECTURE="arm64"
        VOCODER_TARGET="aarch64-apple-darwin"
        ;;
    osx-x64)
        TARGET_PLATFORM="macos"
        MACOS_ARCHITECTURE="x86_64"
        VOCODER_TARGET="x86_64-apple-darwin"
        ;;
    win-x64)
        TARGET_PLATFORM="windows"
        MACOS_ARCHITECTURE=""
        if [[ -n "${DVM_WINDOWS_VOCODER_TARGET:-}" ]]; then
            VOCODER_TARGET="$DVM_WINDOWS_VOCODER_TARGET"
        elif [[ "${OS:-}" == "Windows_NT" ]]; then
            # Git Bash includes MinGW tools on hosted Windows runners, but the
            # native Windows toolchain installed by CI is the MSVC target.
            VOCODER_TARGET="x86_64-pc-windows-msvc"
        elif cargo xwin --version >/dev/null 2>&1; then
            # Match the native Windows and release-CI payload when cargo-xwin
            # is available. The MinGW linker does not currently produce a
            # bit-reproducible DLL, so selecting it merely because Homebrew
            # installed a compiler would make otherwise identical packages
            # differ between builds.
            VOCODER_TARGET="x86_64-pc-windows-msvc"
        elif command -v x86_64-w64-mingw32-gcc >/dev/null 2>&1; then
            # Retain MinGW as the fallback for Unix hosts without cargo-xwin.
            VOCODER_TARGET="x86_64-pc-windows-gnu"
        else
            VOCODER_TARGET="x86_64-pc-windows-msvc"
        fi
        ;;
    win-arm64)
        TARGET_PLATFORM="windows"
        MACOS_ARCHITECTURE=""
        VOCODER_TARGET="${DVM_WINDOWS_VOCODER_TARGET:-aarch64-pc-windows-msvc}"
        ;;
    linux-x64)
        TARGET_PLATFORM="linux"
        MACOS_ARCHITECTURE=""
        VOCODER_TARGET="x86_64-unknown-linux-gnu"
        ;;
    linux-arm64)
        TARGET_PLATFORM="linux"
        MACOS_ARCHITECTURE=""
        VOCODER_TARGET="aarch64-unknown-linux-gnu"
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64\n' >&2
        exit 2
        ;;
esac

VOCODER_CARGO_EXTENSION=""
if [[ "$VOCODER_TARGET" == *-pc-windows-msvc && "${OS:-}" != "Windows_NT" ]] &&
   cargo xwin --version >/dev/null 2>&1; then
    # cargo-xwin supplies the Windows SDK and linker when a Unix release host
    # deliberately cross-builds an MSVC payload (notably Windows ARM64).
    VOCODER_CARGO_EXTENSION="xwin"
fi

# macOS's per-user TMPDIR is exposed through several compiler and linker input
# paths even after normal path mapping. Use the canonical Unix temporary root
# so independent builds see the same logical prefix while mktemp still gives
# each build physically separate output and intermediate directories.
BUILD_ROOT="$(mktemp -d "/tmp/dvmconsole-publish-build.XXXXXX")"
DETERMINISTIC_METADATA_ROOT="/tmp/dvmconsole-package-metadata/$RID"
cleanup_build() {
    rm -rf "$BUILD_ROOT"
    rm -rf "$DETERMINISTIC_METADATA_ROOT"
}
trap cleanup_build EXIT
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$BUILD_ROOT/cargo}"
if [[ -L "$DETERMINISTIC_METADATA_ROOT" ]]; then
    printf 'Deterministic metadata root must not be a symbolic link: %s\n' "$DETERMINISTIC_METADATA_ROOT" >&2
    exit 4
fi
rm -rf "$DETERMINISTIC_METADATA_ROOT"
mkdir -p "$DETERMINISTIC_METADATA_ROOT"

if [[ -n "$MACOS_ARCHITECTURE" ]]; then
    AUDIO_BUILD_DIR="${DVM_AUDIO_BUILD_DIR:-$BUILD_ROOT/dvmaudio}"
    cmake -S "$ROOT_DIR/native/dvmaudio" -B "$AUDIO_BUILD_DIR" \
        -DCMAKE_BUILD_TYPE="$CONFIGURATION" \
        -DCMAKE_OSX_ARCHITECTURES="$MACOS_ARCHITECTURE" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOS_DEPLOYMENT_TARGET"
    cmake --build "$AUDIO_BUILD_DIR" --config "$CONFIGURATION"
fi

if [[ "$TARGET_PLATFORM" == "linux" ]]; then
    host_kernel="$(uname -s)"
    host_architecture="$(uname -m)"
    expected_host_architecture="x86_64"
    if [[ "$RID" == "linux-arm64" ]]; then
        expected_host_architecture="aarch64"
    fi
    if [[ -z "${DVM_LINUX_CMAKE_TOOLCHAIN:-}" &&
          ("$host_kernel" != "Linux" ||
           ("$host_architecture" != "$expected_host_architecture" &&
            !("$expected_host_architecture" == "aarch64" && "$host_architecture" == "arm64"))) ]]; then
        printf '%s requires a %s Linux host or DVM_LINUX_CMAKE_TOOLCHAIN; current host is %s/%s.\n' \
            "$RID" "$expected_host_architecture" "$host_kernel" "$host_architecture" >&2
        exit 3
    fi
    AUDIO_BUILD_DIR="${DVM_AUDIO_BUILD_DIR:-$BUILD_ROOT/dvmaudio-pipewire}"
    CMAKE_ARGUMENTS=(
        -S "$ROOT_DIR/native/dvmaudio-pipewire"
        -B "$AUDIO_BUILD_DIR"
        -DCMAKE_BUILD_TYPE="$CONFIGURATION"
        -DBUILD_TESTING=ON
    )
    if [[ -n "${DVM_LINUX_CMAKE_TOOLCHAIN:-}" ]]; then
        CMAKE_ARGUMENTS+=("-DCMAKE_TOOLCHAIN_FILE=$DVM_LINUX_CMAKE_TOOLCHAIN")
    fi
    cmake "${CMAKE_ARGUMENTS[@]}"
    cmake --build "$AUDIO_BUILD_DIR" --config "$CONFIGURATION" --parallel
    ctest --test-dir "$AUDIO_BUILD_DIR" --output-on-failure
fi

requested_output="$OUTPUT_DIR"
if ! OUTPUT_DIR="$(validate_publish_target "$requested_output")"; then
    printf 'Unsafe publish output directory: %s\n' "$requested_output" >&2
    exit 4
fi
mkdir -p "$(dirname "$OUTPUT_DIR")"
rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

dotnet restore "$PROJECT" \
    --runtime "$RID" \
    --locked-mode \
    --ignore-failed-sources \
    -p:Configuration="$CONFIGURATION" \
    -p:DvmConsoleTargetPlatform="$TARGET_PLATFORM" \
    -p:DvmConsolePackageRuntime="$RID" \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial \
    -p:NuGetAudit=false \
    -p:DvmConsoleIsolatedBuildRoot="$BUILD_ROOT" \
    -p:DvmConsoleDeterministicMetadataRoot="$DETERMINISTIC_METADATA_ROOT" \
    --verbosity minimal
PUBLISH_PROPERTIES=(
    -p:UseAppHost=true
    -p:NativeVocoderTarget="$VOCODER_TARGET"
    -p:DvmConsoleTargetPlatform="$TARGET_PLATFORM"
    -p:DvmConsolePackageRuntime="$RID"
    -p:DebugType=None
    -p:PublishTrimmed=true
    -p:TrimMode=partial
    "-p:DvmConsoleIsolatedBuildRoot=$BUILD_ROOT"
    "-p:DvmConsoleDeterministicMetadataRoot=$DETERMINISTIC_METADATA_ROOT"
)
if [[ -n "$VOCODER_CARGO_EXTENSION" ]]; then
    PUBLISH_PROPERTIES+=("-p:NativeVocoderCargoExtension=$VOCODER_CARGO_EXTENSION")
fi
if [[ -n "${DVM_RELEASE_VERSION:-}" ]]; then
    PUBLISH_PROPERTIES+=(
        "-p:Version=$DVM_RELEASE_VERSION"
        "-p:InformationalVersion=$DVM_RELEASE_VERSION"
    )
fi
if [[ "$RID" == win-* ]]; then
    PUBLISH_PROPERTIES+=(
        -p:PublishSingleFile=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true
    )
fi

dotnet publish "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    --no-restore \
    --output "$OUTPUT_DIR" \
    "${PUBLISH_PROPERTIES[@]}"

if [[ -n "$MACOS_ARCHITECTURE" ]]; then
    cp "$AUDIO_BUILD_DIR/libdvmaudio.dylib" "$OUTPUT_DIR/libdvmaudio.dylib"
elif [[ "$TARGET_PLATFORM" == "linux" ]]; then
    cp "$AUDIO_BUILD_DIR/libdvmaudio-pipewire.so" "$OUTPUT_DIR/libdvmaudio-pipewire.so"
fi

printf 'Published %s to %s\n' "$RID" "$OUTPUT_DIR"
"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$OUTPUT_DIR"
