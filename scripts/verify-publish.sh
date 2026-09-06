#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

RID="${1:-}"
OUTPUT_DIR="${2:-}"
REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MACOS_DEPLOYMENT_TARGET="$(/usr/bin/tr -d '[:space:]' < "$REPOSITORY_ROOT/packaging/macos/deployment-target.txt")"

verify_macos_deployment_target() {
    local description="$1"
    local library_path="$2"
    local require_exact="${3:-false}"
    local build_information
    local minimum_version

    if ! build_information=$(/usr/bin/xcrun vtool -show-build "$library_path" 2>&1); then
        printf 'Unable to inspect %s deployment target: %s\n' "$description" "$build_information" >&2
        exit 9
    fi

    minimum_version=$(printf '%s\n' "$build_information" | /usr/bin/awk '$1 == "minos" { print $2; exit }')
    if [[ -z "$minimum_version" ]]; then
        printf '%s has no readable macOS deployment target.\n' "$description" >&2
        exit 9
    fi
    if ! /usr/bin/awk \
        -v actual="$minimum_version" \
        -v maximum="$MACOS_DEPLOYMENT_TARGET" '
            BEGIN {
                split(actual, actual_parts, ".")
                split(maximum, maximum_parts, ".")
                for (component = 1; component <= 3; component++) {
                    actual_part = actual_parts[component] + 0
                    maximum_part = maximum_parts[component] + 0
                    if (actual_part < maximum_part)
                        exit 0
                    if (actual_part > maximum_part)
                        exit 1
                }
                exit 0
            }
        '; then
        printf '%s requires macOS %s, newer than the %s bundle target.\n' \
            "$description" "$minimum_version" "$MACOS_DEPLOYMENT_TARGET" >&2
        exit 9
    fi
    if [[ "$require_exact" == true && "$minimum_version" != "$MACOS_DEPLOYMENT_TARGET" ]]; then
        printf '%s deployment target is %s instead of macOS %s.\n' \
            "$description" "$minimum_version" "$MACOS_DEPLOYMENT_TARGET" >&2
        exit 9
    fi
}

verify_macos_uuid() {
    local description="$1"
    local library_path="$2"

    if ! /usr/bin/otool -l "$library_path" |
        /usr/bin/awk '$1 == "cmd" && $2 == "LC_UUID" { found = 1 } END { exit(found ? 0 : 1) }'; then
        printf '%s has no LC_UUID load command and cannot be loaded reliably by macOS.\n' \
            "$description" >&2
        exit 9
    fi
}

verify_linux_glibc_ceiling() {
    local output_directory="$1"
    local maximum_version="$2"
    local native_file
    local required_symbol
    local required_version
    local newest_version

    if ! command -v readelf >/dev/null 2>&1; then
        printf 'Linux publish verification requires readelf from binutils.\n' >&2
        exit 12
    fi

    while IFS= read -r -d '' native_file; do
        if ! /usr/bin/file "$native_file" | /usr/bin/grep -q ELF; then
            continue
        fi

        while IFS= read -r required_symbol; do
            [[ -n "$required_symbol" ]] || continue
            required_version="${required_symbol#GLIBC_}"
            newest_version=$(
                printf '%s\n%s\n' "$maximum_version" "$required_version" |
                    /usr/bin/sort -V |
                    /usr/bin/tail -n 1
            )
            if [[ "$newest_version" != "$maximum_version" ]]; then
                printf '%s requires %s, newer than the Linux package ceiling GLIBC_%s.\n' \
                    "$native_file" "$required_symbol" "$maximum_version" >&2
                printf 'Build Linux packages with scripts/build-linux-portable.sh.\n' >&2
                exit 12
            fi
        done < <(
            readelf --version-info "$native_file" 2>/dev/null |
                /usr/bin/grep -oE 'GLIBC_[0-9]+(\.[0-9]+)+' |
                /usr/bin/sort -Vu || true
        )
    done < <(
        /usr/bin/find "$output_directory" -type f \
            \( -name DvmConsole -o -name '*.so' -o -name '*.so.*' \) \
            -print0
    )
}

if [[ -z "$RID" || -z "$OUTPUT_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|osx-x64|win-x64|win-arm64|linux-x64|linux-arm64> <publish-directory>\n' "${0##*/}" >&2
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
        EXPECTED_LINUX_ARCHITECTURE=""
        EXPECTED_WINDOWS_ARCHITECTURE="x64"
        ;;
    win-arm64)
        EXPECTED_MACOS_ARCHITECTURE=""
        EXPECTED_LINUX_ARCHITECTURE=""
        EXPECTED_WINDOWS_ARCHITECTURE="ARM64"
        ;;
    linux-x64)
        EXPECTED_MACOS_ARCHITECTURE=""
        EXPECTED_LINUX_ARCHITECTURE="x86-64"
        MAXIMUM_LINUX_GLIBC_VERSION="2.34"
        ;;
    linux-arm64)
        EXPECTED_MACOS_ARCHITECTURE=""
        EXPECTED_LINUX_ARCHITECTURE="ARM aarch64"
        MAXIMUM_LINUX_GLIBC_VERSION="2.34"
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, osx-x64, win-x64, win-arm64, linux-x64, linux-arm64\n' >&2
        exit 2
        ;;
esac

python3 "$REPOSITORY_ROOT/scripts/verify-package.py" \
    --publish-only --publish-root "$OUTPUT_DIR" --rid "$RID"

DEMO_CODEPLUG="$OUTPUT_DIR/Demo/codeplug.yml"
EXPECTED_DEMO_CODEPLUG="$REPOSITORY_ROOT/configs/codeplug.demo.yml"
if [[ ! -f "$DEMO_CODEPLUG" ]] ||
   ! /usr/bin/cmp -s "$EXPECTED_DEMO_CODEPLUG" "$DEMO_CODEPLUG"; then
    printf 'Publish is missing the exact sanitized network-disabled demonstration codeplug.\n' >&2
    exit 4
fi

if /usr/bin/find "$OUTPUT_DIR" -type f -name 'AvaloniaUI.DiagnosticsSupport*' -print -quit | /usr/bin/grep -q .; then
    printf 'Publish contains the Debug-only Avalonia diagnostics package.\n' >&2
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
        while IFS= read -r -d '' native_file; do
            if /usr/bin/file "$native_file" | /usr/bin/grep -q 'Mach-O'; then
                native_description=$(/usr/bin/file "$native_file")
                if [[ "$native_description" != *"$EXPECTED_MACOS_ARCHITECTURE"* ]]; then
                    printf 'Published Mach-O is not %s: %s\n' "$EXPECTED_MACOS_ARCHITECTURE" "$native_description" >&2
                    exit 9
                fi
                verify_macos_deployment_target "published Mach-O $native_file" "$native_file"
                verify_macos_uuid "published Mach-O $native_file" "$native_file"
            fi
        done < <(/usr/bin/find "$OUTPUT_DIR" -type f -print0)
        for manifest_and_library in \
            "native/dvmaudio/managed-exports.txt:libdvmaudio.dylib:dvm_audio_" \
            "native/vocoder/managed-exports.txt:libdvmconsole_vocoder.dylib:dvmconsole_vocoder_"; do
            IFS=: read -r manifest library prefix <<< "$manifest_and_library"
            actual_exports="$(mktemp "${TMPDIR:-/tmp}/dvmconsole-exports.XXXXXX")"
            /usr/bin/nm -gU "$OUTPUT_DIR/$library" | /usr/bin/awk '{ print $NF }' |
                /usr/bin/sed 's/^_//' | /usr/bin/grep "^$prefix" | /usr/bin/sort -u > "$actual_exports"
            if ! /usr/bin/diff -u "$REPOSITORY_ROOT/$manifest" "$actual_exports"; then
                printf '%s exports do not match %s.\n' "$library" "$manifest" >&2
                rm -f "$actual_exports"
                exit 9
            fi
            rm -f "$actual_exports"
        done
        ;;
    linux-x64|linux-arm64)
        if [[ ! -x "$OUTPUT_DIR/DvmConsole" ]]; then
            printf 'Linux publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole" >&2
            exit 4
        fi
        for file_name in DvmConsole.dll DvmConsole.deps.json DvmConsole.runtimeconfig.json; do
            if [[ ! -f "$OUTPUT_DIR/$file_name" ]]; then
                printf 'Missing required publish file: %s\n' "$OUTPUT_DIR/$file_name" >&2
                exit 4
            fi
        done
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole")
        if [[ "$apphost_description" != *ELF* || "$apphost_description" != *"$EXPECTED_LINUX_ARCHITECTURE"* ]]; then
            printf 'Linux apphost is not %s ELF: %s\n' "$EXPECTED_LINUX_ARCHITECTURE" "$apphost_description" >&2
            exit 4
        fi

        while IFS= read -r -d '' native_file; do
            native_description=$(/usr/bin/file "$native_file")
            if [[ "$native_description" == *ELF* && "$native_description" != *"$EXPECTED_LINUX_ARCHITECTURE"* ]]; then
                printf 'Published ELF is not %s: %s\n' "$EXPECTED_LINUX_ARCHITECTURE" "$native_description" >&2
                exit 12
            fi
        done < <(/usr/bin/find "$OUTPUT_DIR" -type f -print0)

        if [[ -e "$OUTPUT_DIR/libdvmaudio.dylib" || -e "$OUTPUT_DIR/dvmconsole_vocoder.dll" ]]; then
            printf 'Linux publish contains a native library for another platform.\n' >&2
            exit 11
        fi

        native_audio="$OUTPUT_DIR/libdvmaudio-pipewire.so"
        native_vocoder="$OUTPUT_DIR/libdvmconsole_vocoder.so"
        for native_library in "$native_audio" "$native_vocoder"; do
            if [[ ! -f "$native_library" ]]; then
                printf 'Missing required Linux native library: %s\n' "$native_library" >&2
                exit 12
            fi
            native_description=$(/usr/bin/file "$native_library")
            if [[ "$native_description" != *ELF* || "$native_description" != *"$EXPECTED_LINUX_ARCHITECTURE"* ]]; then
                printf 'Linux native library is not %s ELF: %s\n' "$EXPECTED_LINUX_ARCHITECTURE" "$native_description" >&2
                exit 12
            fi
        done

        if ! readelf -d "$native_audio" | /usr/bin/grep -q 'libpipewire-0.3.so'; then
            printf 'Linux audio shim does not declare its PipeWire runtime dependency.\n' >&2
            exit 12
        fi

        for manifest_and_library in \
            "native/dvmaudio-pipewire/managed-exports.txt:libdvmaudio-pipewire.so:dvm_audio_" \
            "native/vocoder/managed-exports.txt:libdvmconsole_vocoder.so:dvmconsole_vocoder_"; do
            IFS=: read -r manifest library prefix <<< "$manifest_and_library"
            actual_exports="$(mktemp "${TMPDIR:-/tmp}/dvmconsole-exports.XXXXXX")"
            nm -D --defined-only "$OUTPUT_DIR/$library" | /usr/bin/awk '{ print $NF }' |
                /usr/bin/grep "^$prefix" | /usr/bin/sort -u > "$actual_exports"
            if ! /usr/bin/diff -u "$REPOSITORY_ROOT/$manifest" "$actual_exports"; then
                printf '%s exports do not match %s.\n' "$library" "$manifest" >&2
                rm -f "$actual_exports"
                exit 12
            fi
            rm -f "$actual_exports"
        done

        verify_linux_glibc_ceiling "$OUTPUT_DIR" "$MAXIMUM_LINUX_GLIBC_VERSION"
        ;;
    win-x64|win-arm64)
        if [[ ! -f "$OUTPUT_DIR/DvmConsole.exe" ]]; then
            printf 'Windows publish is missing an executable apphost: %s\n' "$OUTPUT_DIR/DvmConsole.exe" >&2
            exit 4
        fi
        apphost_description=$(/usr/bin/file "$OUTPUT_DIR/DvmConsole.exe")
        if [[ "$RID" == "win-x64" ]]; then
            if [[ "$apphost_description" != *x86-64* && "$apphost_description" != *x86_64* ]]; then
                printf 'Windows apphost is not x64: %s\n' "$apphost_description" >&2
                exit 4
            fi
        elif [[ "$apphost_description" != *Aarch64* &&
                "$apphost_description" != *aarch64* &&
                "$apphost_description" != *ARM64* ]]; then
            printf 'Windows apphost is not ARM64: %s\n' "$apphost_description" >&2
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
    if [[ "$file_name" == "$DEMO_CODEPLUG" ]]; then
        continue
    fi
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
        verify_macos_deployment_target "macOS audio shim" "$native_library" true

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
        verify_macos_deployment_target "macOS vocoder" "$native_vocoder" true
        ;;
    win-x64|win-arm64)
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
