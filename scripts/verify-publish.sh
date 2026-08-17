#!/usr/bin/env bash
set -euo pipefail

RID="${1:-}"
OUTPUT_DIR="${2:-}"

verify_macos_deployment_target() {
    local description="$1"
    local library_path="$2"
    local build_information
    local minimum_version

    if ! build_information=$(/usr/bin/xcrun vtool -show-build "$library_path" 2>&1); then
        printf 'Unable to inspect %s deployment target: %s\n' "$description" "$build_information" >&2
        exit 9
    fi

    minimum_version=$(printf '%s\n' "$build_information" | /usr/bin/awk '$1 == "minos" { print $2; exit }')
    if [[ "$minimum_version" != "14.0" ]]; then
        printf '%s deployment target is not macOS 14.0: %s\n' "$description" "${minimum_version:-unknown}" >&2
        exit 9
    fi
}

if [[ -z "$RID" || -z "$OUTPUT_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|osx-x64|win-x64> <publish-directory>\n' "${0##*/}" >&2
    exit 2
fi

case "$RID" in
    osx-arm64)
        EXPECTED_MACOS_ARCHITECTURE="arm64"
        ;;
    osx-x64)
        EXPECTED_MACOS_ARCHITECTURE="x86_64"
        ;;
    win-x64)
        EXPECTED_MACOS_ARCHITECTURE=""
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, osx-x64, win-x64\n' >&2
        exit 2
        ;;
esac

if [[ ! -d "$OUTPUT_DIR" ]]; then
    printf 'Publish directory does not exist: %s\n' "$OUTPUT_DIR" >&2
    exit 3
fi

if [[ -n "$EXPECTED_MACOS_ARCHITECTURE" ]]; then
    for file_name in DvmConsole.dll DvmConsole.deps.json DvmConsole.runtimeconfig.json; do
        if [[ ! -f "$OUTPUT_DIR/$file_name" ]]; then
            printf 'Missing required publish file: %s\n' "$OUTPUT_DIR/$file_name" >&2
            exit 4
        fi
    done
fi

if [[ -e "$OUTPUT_DIR/Docs" ]]; then
    printf 'Publish contains documentation that must be read live from GitHub.\n' >&2
    exit 4
fi

for legacy_alert in alert1.wav alert2.wav alert3.wav; do
    if [[ -e "$OUTPUT_DIR/Audio/$legacy_alert" ]]; then
        printf 'Publish contains obsolete generated-alert asset: %s\n' "$OUTPUT_DIR/Audio/$legacy_alert" >&2
        exit 4
    fi
done

case "$RID" in
    osx-arm64|osx-x64)
        if [[ ! -x "$OUTPUT_DIR/DvmConsole" ]]; then
            printf 'macOS publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole" >&2
            exit 4
        fi
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole")
        if [[ "$apphost_description" != *"$EXPECTED_MACOS_ARCHITECTURE"* ]]; then
            printf 'macOS apphost is not %s: %s\n' "$EXPECTED_MACOS_ARCHITECTURE" "$apphost_description" >&2
            exit 4
        fi
        ;;
    win-x64)
        if [[ ! -f "$OUTPUT_DIR/DvmConsole.exe" ]]; then
            printf 'Windows publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole.exe" >&2
            exit 4
        fi
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole.exe")
        if [[ "$apphost_description" != *x86-64* && "$apphost_description" != *x86_64* ]]; then
            printf 'Windows apphost is not x64: %s\n' "$apphost_description" >&2
            exit 4
        fi
        if [[ -e "$OUTPUT_DIR/DvmConsole.dll" ||
              -e "$OUTPUT_DIR/DvmConsole.deps.json" ||
              -e "$OUTPUT_DIR/DvmConsole.runtimeconfig.json" ]]; then
            printf 'Windows publish is not a clean single-file application.\n' >&2
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
    osx-arm64|osx-x64)
        native_library="$OUTPUT_DIR/libdvmaudio.dylib"
        if [[ ! -f "$native_library" ]]; then
            printf 'Missing required macOS audio shim: %s\n' "$native_library" >&2
            exit 7
        fi

        native_description=$(/usr/bin/file "$native_library")
        if [[ "$native_description" != *"$EXPECTED_MACOS_ARCHITECTURE"* ]]; then
            printf 'macOS audio shim is not %s: %s\n' "$EXPECTED_MACOS_ARCHITECTURE" "$native_description" >&2
            exit 8
        fi
        verify_macos_deployment_target "macOS audio shim" "$native_library"

        native_vocoder="$OUTPUT_DIR/libdvmconsole_vocoder.dylib"
        if [[ ! -f "$native_vocoder" ]]; then
            printf 'Missing required macOS vocoder: %s\n' "$native_vocoder" >&2
            exit 9
        fi
        native_description=$(/usr/bin/file "$native_vocoder")
        if [[ "$native_description" != *"$EXPECTED_MACOS_ARCHITECTURE"* ]]; then
            printf 'macOS vocoder is not %s: %s\n' "$EXPECTED_MACOS_ARCHITECTURE" "$native_description" >&2
            exit 9
        fi
        verify_macos_deployment_target "macOS vocoder" "$native_vocoder"
        ;;
    win-x64)
        if [[ -e "$OUTPUT_DIR/libdvmaudio.dylib" ]]; then
            printf 'Windows publish contains the macOS audio shim.\n' >&2
            exit 11
        fi

        if [[ -e "$OUTPUT_DIR/dvmconsole_vocoder.dll" || -e "$OUTPUT_DIR/libvocoder.dll" ]]; then
            printf 'Windows vocoder must be embedded in DvmConsole.exe, not shipped as a sidecar.\n' >&2
            exit 10
        fi
        ;;
esac

printf 'Publish verification passed: %s (%s)\n' "$OUTPUT_DIR" "$RID"
