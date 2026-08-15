#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
OUTPUT_DIR="${2:-}"
ALLOW_MISSING_VOCODER="${DVM_ALLOW_MISSING_VOCODER:-0}"

if [[ -z "$RID" || -z "$OUTPUT_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|win-x64> <publish-directory>\n' "${0##*/}" >&2
    exit 2
fi

case "$RID" in
    osx-arm64|win-x64)
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, win-x64\n' >&2
        exit 2
        ;;
esac

if [[ ! -d "$OUTPUT_DIR" ]]; then
    printf 'Publish directory does not exist: %s\n' "$OUTPUT_DIR" >&2
    exit 3
fi

required_files=(
    DvmConsole.Desktop.dll
    DvmConsole.Desktop.deps.json
    DvmConsole.Desktop.runtimeconfig.json
)

for file_name in "${required_files[@]}"; do
    if [[ ! -f "$OUTPUT_DIR/$file_name" ]]; then
        printf 'Missing required publish file: %s\n' "$OUTPUT_DIR/$file_name" >&2
        exit 4
    fi
done

for legacy_alert in alert1.wav alert2.wav alert3.wav; do
    if [[ -e "$OUTPUT_DIR/Audio/$legacy_alert" ]]; then
        printf 'Publish contains obsolete generated-alert asset: %s\n' "$OUTPUT_DIR/Audio/$legacy_alert" >&2
        exit 4
    fi
done

case "$RID" in
    osx-arm64)
        if [[ ! -x "$OUTPUT_DIR/DvmConsole.Desktop" ]]; then
            printf 'macOS publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole.Desktop" >&2
            exit 4
        fi
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole.Desktop")
        if [[ "$apphost_description" != *arm64* ]]; then
            printf 'macOS apphost is not arm64: %s\n' "$apphost_description" >&2
            exit 4
        fi
        ;;
    win-x64)
        if [[ ! -f "$OUTPUT_DIR/DvmConsole.Desktop.exe" ]]; then
            printf 'Windows publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole.Desktop.exe" >&2
            exit 4
        fi
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole.Desktop.exe")
        if [[ "$apphost_description" != *x86-64* && "$apphost_description" != *x86_64* ]]; then
            printf 'Windows apphost is not x64: %s\n' "$apphost_description" >&2
            exit 4
        fi
        ;;
esac

if /usr/bin/find "$OUTPUT_DIR" -type f \( -name 'codeplug_testing.yml' -o -name 'codeplug_testing.yaml' \) -print -quit | /usr/bin/grep -q .; then
    printf 'Publish contains the testing codeplug; remove it before handoff.\n' >&2
    exit 5
fi

text_files=()
while IFS= read -r file_name; do
    text_files+=("$file_name")
done < <(/usr/bin/find "$OUTPUT_DIR" -type f \( -name '*.json' -o -name '*.yml' -o -name '*.yaml' -o -name '*.config' -o -name '*.txt' \) -print)

if ((${#text_files[@]} > 0)) && /usr/bin/grep -Eiq '10\.10\.10\.55|preshared|authPassword|password' "${text_files[@]}"; then
    printf 'Publish contains credential-like or test-endpoint material.\n' >&2
    exit 6
fi

case "$RID" in
    osx-arm64)
        native_library="$OUTPUT_DIR/libdvmaudio.dylib"
        if [[ ! -f "$native_library" ]]; then
            printf 'Missing required macOS audio shim: %s\n' "$native_library" >&2
            exit 7
        fi

        native_description=$(/usr/bin/file "$native_library")
        if [[ "$native_description" != *arm64* ]]; then
            printf 'macOS audio shim is not arm64: %s\n' "$native_description" >&2
            exit 8
        fi

        native_vocoder="$OUTPUT_DIR/libvocoder.dylib"
        if [[ ! -f "$native_vocoder" ]]; then
            if [[ "$ALLOW_MISSING_VOCODER" != "1" ]]; then
                printf 'Missing macOS vocoder: %s\n' "$native_vocoder" >&2
                printf 'Set DVMVOCODER_LIBRARY or DVM_ALLOW_MISSING_VOCODER=1 for a UI-only artifact.\n' >&2
                exit 9
            fi
        else
            native_description=$(/usr/bin/file "$native_vocoder")
            if [[ "$native_description" != *arm64* ]]; then
                printf 'macOS vocoder is not arm64: %s\n' "$native_description" >&2
                exit 9
            fi
        fi
        ;;
    win-x64)
        if [[ -e "$OUTPUT_DIR/libdvmaudio.dylib" ]]; then
            printf 'Windows publish contains the macOS audio shim.\n' >&2
            exit 11
        fi

        native_vocoder="$OUTPUT_DIR/libvocoder.dll"
        if [[ ! -f "$native_vocoder" ]]; then
            if [[ "$ALLOW_MISSING_VOCODER" != "1" ]]; then
                printf 'Missing Windows vocoder: %s\n' "$native_vocoder" >&2
                printf 'Set DVMVOCODER_LIBRARY or DVM_ALLOW_MISSING_VOCODER=1 for a UI-only artifact.\n' >&2
                exit 10
            fi
        else
            optional_description=$(/usr/bin/file "$native_vocoder")
            if [[ "$optional_description" != *x86-64* && "$optional_description" != *x86_64* ]]; then
                printf 'Windows vocoder is not x64: %s\n' "$optional_description" >&2
                exit 10
            fi
        fi
        ;;
esac

printf 'Publish verification passed: %s (%s)\n' "$OUTPUT_DIR" "$RID"
