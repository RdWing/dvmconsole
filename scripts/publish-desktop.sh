#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"
RID="${1:-osx-arm64}"
OUTPUT_DIR="${2:-$ROOT_DIR/artifacts/$RID}"
CONFIGURATION="${CONFIGURATION:-Release}"
MACOS_DEPLOYMENT_TARGET="14.0"

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
        VOCODER_TARGET="x86_64-pc-windows-msvc"
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, osx-x64, win-x64\n' >&2
        exit 2
        ;;
esac

if [[ -n "$MACOS_ARCHITECTURE" ]]; then
    AUDIO_BUILD_DIR="${DVM_AUDIO_BUILD_DIR:-$ROOT_DIR/native/dvmaudio/build/$RID}"
    cmake -S "$ROOT_DIR/native/dvmaudio" -B "$AUDIO_BUILD_DIR" \
        -DCMAKE_BUILD_TYPE="$CONFIGURATION" \
        -DCMAKE_OSX_ARCHITECTURES="$MACOS_ARCHITECTURE" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="$MACOS_DEPLOYMENT_TARGET"
    cmake --build "$AUDIO_BUILD_DIR" --config "$CONFIGURATION"
fi

mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR/Audio/alert1.wav" "$OUTPUT_DIR/Audio/alert2.wav" "$OUTPUT_DIR/Audio/alert3.wav"
rm -f "$OUTPUT_DIR/libvocoder.dylib" "$OUTPUT_DIR/libvocoder.dll"

dotnet restore "$PROJECT" \
    --runtime "$RID" \
    --force-evaluate \
    --ignore-failed-sources \
    -p:Configuration="$CONFIGURATION" \
    -p:DvmConsoleTargetPlatform="$TARGET_PLATFORM" \
    -p:PublishTrimmed=true \
    -p:TrimMode=partial \
    -p:NuGetAudit=false \
    --verbosity minimal
PUBLISH_PROPERTIES=(
    -p:UseAppHost=true
    -p:NativeVocoderTarget="$VOCODER_TARGET"
    -p:DvmConsoleTargetPlatform="$TARGET_PLATFORM"
    -p:DebugType=None
    -p:PublishTrimmed=true
    -p:TrimMode=partial
)
if [[ "$RID" == "win-x64" ]]; then
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
fi

printf 'Published %s to %s\n' "$RID" "$OUTPUT_DIR"
"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$OUTPUT_DIR"
