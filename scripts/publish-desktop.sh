#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PROJECT="$ROOT_DIR/src/DvmConsole.Desktop/DvmConsole.Desktop.csproj"
RID="${1:-osx-arm64}"
OUTPUT_DIR="${2:-$ROOT_DIR/artifacts/$RID}"
CONFIGURATION="${CONFIGURATION:-Release}"
VOCODER_LIBRARY="${DVMVOCODER_LIBRARY:-}"

if [[ "$RID" != "osx-arm64" && "$RID" != "win-x64" ]]; then
    printf 'Supported runtime identifiers: osx-arm64, win-x64\n' >&2
    exit 2
fi

if [[ "$RID" == "osx-arm64" ]]; then
    AUDIO_BUILD_DIR="${DVM_AUDIO_BUILD_DIR:-$ROOT_DIR/native/dvmaudio/build/$RID}"
    cmake -S "$ROOT_DIR/native/dvmaudio" -B "$AUDIO_BUILD_DIR" -DCMAKE_BUILD_TYPE="$CONFIGURATION"
    cmake --build "$AUDIO_BUILD_DIR" --config "$CONFIGURATION"
fi

dotnet restore "$PROJECT" --runtime "$RID" --ignore-failed-sources -p:NuGetAudit=false --verbosity minimal
dotnet publish "$PROJECT" \
    --configuration "$CONFIGURATION" \
    --runtime "$RID" \
    --self-contained false \
    --no-restore \
    --output "$OUTPUT_DIR" \
    /p:UseAppHost=false

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
    printf 'Warning: no native vocoder was copied; DMR/P25 voice will be unavailable.\n' >&2
fi

printf 'Published %s to %s\n' "$RID" "$OUTPUT_DIR"
