#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"
RID="${1:-osx-arm64}"
OUTPUT_DIR="${2:-$ROOT_DIR/artifacts/$RID}"
CONFIGURATION="${CONFIGURATION:-Release}"
VOCODER_LIBRARY="${DVMVOCODER_LIBRARY:-}"
ALLOW_MISSING_VOCODER="${DVM_ALLOW_MISSING_VOCODER:-0}"

if [[ "$RID" != "osx-arm64" && "$RID" != "win-x64" ]]; then
    printf 'Supported runtime identifiers: osx-arm64, win-x64\n' >&2
    exit 2
fi

if [[ "$RID" == "osx-arm64" ]]; then
    AUDIO_BUILD_DIR="${DVM_AUDIO_BUILD_DIR:-$ROOT_DIR/native/dvmaudio/build/$RID}"
    cmake -S "$ROOT_DIR/native/dvmaudio" -B "$AUDIO_BUILD_DIR" -DCMAKE_BUILD_TYPE="$CONFIGURATION"
    cmake --build "$AUDIO_BUILD_DIR" --config "$CONFIGURATION"
fi

mkdir -p "$OUTPUT_DIR"
rm -f "$OUTPUT_DIR/Audio/alert1.wav" "$OUTPUT_DIR/Audio/alert2.wav" "$OUTPUT_DIR/Audio/alert3.wav"
case "$RID" in
    osx-arm64)
        rm -f "$OUTPUT_DIR/libvocoder.dylib"
        ;;
    win-x64)
        rm -f "$OUTPUT_DIR/libvocoder.dll"
        ;;
esac

dotnet restore "$PROJECT" --runtime "$RID" --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
PUBLISH_PROPERTIES=(-p:UseAppHost=true)
if [[ "$RID" == "win-x64" ]]; then
    PUBLISH_PROPERTIES+=(
        -p:PublishSingleFile=true
        -p:IncludeNativeLibrariesForSelfExtract=true
        -p:EnableCompressionInSingleFile=true
        -p:DebugType=None
    )
fi

dotnet publish "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained true \
    --no-restore \
    --output "$OUTPUT_DIR" \
    "${PUBLISH_PROPERTIES[@]}"

if [[ "$RID" == "osx-arm64" ]]; then
    cp "$AUDIO_BUILD_DIR/libdvmaudio.dylib" "$OUTPUT_DIR/libdvmaudio.dylib"
fi

if [[ -n "$VOCODER_LIBRARY" ]]; then
    if [[ ! -f "$VOCODER_LIBRARY" ]]; then
        printf 'DVMVOCODER_LIBRARY does not point to a file: %s\n' "$VOCODER_LIBRARY" >&2
        exit 3
    fi

    case "$RID" in
        osx-arm64)
            VOCODER_OUTPUT="libvocoder.dylib"
            ;;
        win-x64)
            VOCODER_OUTPUT="libvocoder.dll"
            ;;
    esac

    cp "$VOCODER_LIBRARY" "$OUTPUT_DIR/$VOCODER_OUTPUT"
else
    if [[ "$ALLOW_MISSING_VOCODER" != "1" ]]; then
        printf 'DVMVOCODER_LIBRARY is required for a working digital-voice package.\n' >&2
        printf 'Build the native vocoder, set DVMVOCODER_LIBRARY, or set DVM_ALLOW_MISSING_VOCODER=1 for a UI-only artifact.\n' >&2
        exit 4
    fi

    printf 'Warning: no native vocoder was copied; this is a UI-only artifact.\n' >&2
fi

printf 'Published %s to %s\n' "$RID" "$OUTPUT_DIR"
"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$OUTPUT_DIR"
