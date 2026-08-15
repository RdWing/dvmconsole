#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
OUTPUT_DIR="${2:-}"

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

        optional_library="$OUTPUT_DIR/libvocoder.dylib"
        if [[ -f "$optional_library" ]]; then
            optional_description=$(/usr/bin/file "$optional_library")
            if [[ "$optional_description" != *arm64* ]]; then
                printf 'Optional macOS vocoder is not arm64: %s\n' "$optional_description" >&2
                exit 9
            fi
        fi
        ;;
    win-x64)
        if [[ -e "$OUTPUT_DIR/libdvmaudio.dylib" ]]; then
            printf 'Windows publish contains the macOS audio shim.\n' >&2
            exit 11
        fi

        optional_library="$OUTPUT_DIR/libvocoder.dll"
        if [[ -f "$optional_library" ]]; then
            optional_description=$(/usr/bin/file "$optional_library")
            if [[ "$optional_description" != *x86-64* && "$optional_description" != *x86_64* ]]; then
                printf 'Optional Windows vocoder is not x64: %s\n' "$optional_description" >&2
                exit 10
            fi
        fi
        ;;
esac

printf 'Publish verification passed: %s (%s)\n' "$OUTPUT_DIR" "$RID"
