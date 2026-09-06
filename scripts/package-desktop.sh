#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MACOS_DEPLOYMENT_TARGET="$(/usr/bin/tr -d '[:space:]' < "$ROOT_DIR/packaging/macos/deployment-target.txt")"
RID="${1:-}"
PUBLISH_DIR="${2:-}"
OUTPUT_PATH="${3:-$ROOT_DIR/artifacts/dvmconsole-$RID.zip}"
APP_OUTPUT="${4:-}"
ARCHIVE_WAS_SUPPLIED=$([[ $# -ge 3 ]] && printf true || printf false)
APP_WAS_SUPPLIED=$([[ $# -ge 4 ]] && printf true || printf false)

if [[ -z "$RID" || -z "$PUBLISH_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|osx-x64|win-x64|win-arm64> <publish-directory> [zip-output] [macos-app-output]\n' "${0##*/}" >&2
    exit 2
fi

case "$RID" in
    osx-arm64)
        EXPECTED_MACOS_ARCHITECTURE="arm64"
        ;;
    osx-x64)
        EXPECTED_MACOS_ARCHITECTURE="x86_64"
        ;;
    win-x64|win-arm64)
        EXPECTED_MACOS_ARCHITECTURE=""
        ;;
    *)
        printf 'Supported runtime identifiers: osx-arm64, osx-x64, win-x64, win-arm64\n' >&2
        exit 2
        ;;
esac

if [[ ! -d "$PUBLISH_DIR" ]]; then
    printf 'Publish directory does not exist: %s\n' "$PUBLISH_DIR" >&2
    exit 3
fi

validate_package_target() {
    local requested="$1"
    local expected_extension="$2"
    local allow_repository_target="${3:-false}"
    local arguments=(
        --target "$requested"
        --extension "$expected_extension"
        --repository-root "$ROOT_DIR"
        --publish-root "$PUBLISH_DIR"
        --staging-root "$STAGING_DIR"
    )
    if [[ "$allow_repository_target" == true ]]; then
        arguments+=(--allow-repository-target)
    fi
    python3 "$ROOT_DIR/scripts/validate-package-target.py" "${arguments[@]}"
}

"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$PUBLISH_DIR"

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/dvmconsole-package.XXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

requested_archive="$OUTPUT_PATH"
if ! OUTPUT_PATH="$(validate_package_target "$requested_archive" .zip "$([[ "$ARCHIVE_WAS_SUPPLIED" == false ]] && printf true || printf false)")"; then
    printf 'Unsafe ZIP output target: %s\n' "$requested_archive" >&2
    exit 12
fi
mkdir -p "$(dirname "$OUTPUT_PATH")"

if [[ -n "$EXPECTED_MACOS_ARCHITECTURE" ]]; then
    if [[ -z "$APP_OUTPUT" ]]; then
        APP_OUTPUT="$(dirname "$OUTPUT_PATH")/DVMConsole.app"
    fi
    requested_app="$APP_OUTPUT"
    if ! APP_OUTPUT="$(validate_package_target "$requested_app" .app "$([[ "$APP_WAS_SUPPLIED" == false && "$ARCHIVE_WAS_SUPPLIED" == false ]] && printf true || printf false)")"; then
        printf 'Unsafe macOS application output target: %s\n' "$requested_app" >&2
        exit 12
    fi
    mkdir -p "$(dirname "$APP_OUTPUT")"

    APP_PATH="$STAGING_DIR/DVMConsole.app"
    mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
    cp "$ROOT_DIR/packaging/macos/Info.plist" "$APP_PATH/Contents/Info.plist"
    if [[ -n "${DVM_RELEASE_VERSION:-}" ]]; then
        if ! bundle_version="$(python3 "$ROOT_DIR/scripts/release_metadata.py" version-core \
            --version "$DVM_RELEASE_VERSION")"; then
            printf 'DVM_RELEASE_VERSION is not a valid bundle version: %s\n' "$DVM_RELEASE_VERSION" >&2
            exit 12
        fi
        # Apple bundle metadata remains a numeric SemVer core even when the
        # package and GitHub release carry an alpha, beta, or RC identifier.
        /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $bundle_version" "$APP_PATH/Contents/Info.plist"
        /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $bundle_version" "$APP_PATH/Contents/Info.plist"
    fi
    cp "$ROOT_DIR/packaging/macos/DVMConsole.icns" "$APP_PATH/Contents/Resources/DVMConsole.icns"
    plutil -lint "$APP_PATH/Contents/Info.plist" >/dev/null
    bundle_requires_explicit_autofill=$(/usr/libexec/PlistBuddy -c \
        'Print :NSAutoFillRequiresTextContentTypeForOneTimeCodeOnMac' \
        "$APP_PATH/Contents/Info.plist")
    if [[ "$bundle_requires_explicit_autofill" != "true" ]]; then
        printf 'macOS bundle does not restrict security-code AutoFill to explicitly annotated fields.\n' >&2
        exit 12
    fi
    /usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c 'Print :NSLocalNetworkUsageDescription' "$APP_PATH/Contents/Info.plist" >/dev/null
    bundle_minimum_version=$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$APP_PATH/Contents/Info.plist")
    if [[ "$bundle_minimum_version" != "$MACOS_DEPLOYMENT_TARGET" ]]; then
        printf 'macOS bundle minimum version is not %s: %s\n' \
            "$MACOS_DEPLOYMENT_TARGET" "$bundle_minimum_version" >&2
        exit 12
    fi
    bundle_icon=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$APP_PATH/Contents/Info.plist")
    if [[ "$bundle_icon" != "DVMConsole.icns" || ! -f "$APP_PATH/Contents/Resources/$bundle_icon" ]]; then
        printf 'macOS bundle is missing its application icon.\n' >&2
        exit 12
    fi
    # LaunchServices must start the real Cocoa/.NET apphost directly. A shell
    # wrapper that execs an apphost from Resources works in Terminal but exits
    # or aborts when Finder owns the application lifecycle.
    cp -R "$PUBLISH_DIR"/. "$APP_PATH/Contents/MacOS/"
    mv "$APP_PATH/Contents/MacOS/DvmConsole" "$APP_PATH/Contents/MacOS/DVM Console"
    chmod 755 "$APP_PATH/Contents/MacOS/DVM Console"
    bundle_executable=$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_PATH/Contents/Info.plist")
    if [[ "$bundle_executable" != "DVM Console" || ! -x "$APP_PATH/Contents/MacOS/$bundle_executable" ]]; then
        printf 'macOS bundle does not launch the real apphost from Contents/MacOS.\n' >&2
        exit 12
    fi
    bundle_apphost_description=$(/usr/bin/file "$APP_PATH/Contents/MacOS/$bundle_executable")
    if [[ "$bundle_apphost_description" != *"$EXPECTED_MACOS_ARCHITECTURE"* ]]; then
        printf 'macOS bundle executable is not %s: %s\n' "$EXPECTED_MACOS_ARCHITECTURE" "$bundle_apphost_description" >&2
        exit 12
    fi
    APP_REPLACEMENT="$STAGING_DIR/DVMConsole-output.app"
    # Preserve the apphost's executable mode even under a restrictive caller
    # umask; LaunchServices reports a non-executable apphost as missing.
    cp -pR "$APP_PATH" "$APP_REPLACEMENT"
    if [[ ! -x "$APP_REPLACEMENT/Contents/MacOS/$bundle_executable" ]]; then
        printf 'macOS application replacement lost its executable mode.\n' >&2
        exit 12
    fi
    APP_BACKUP=""
    if [[ -e "$APP_OUTPUT" ]]; then
        APP_BACKUP="$STAGING_DIR/DVMConsole-previous.app"
        mv "$APP_OUTPUT" "$APP_BACKUP"
    fi
    if ! mv "$APP_REPLACEMENT" "$APP_OUTPUT"; then
        [[ -z "$APP_BACKUP" ]] || mv "$APP_BACKUP" "$APP_OUTPUT"
        exit 12
    fi
    [[ -z "$APP_BACKUP" ]] || rm -rf "$APP_BACKUP"
    PACKAGE_ROOT="$APP_PATH"
else
    PACKAGE_ROOT="$STAGING_DIR/DVMConsole-$RID"
    mkdir -p "$PACKAGE_ROOT"
    cp -R "$PUBLISH_DIR"/. "$PACKAGE_ROOT/"
fi

TEMP_ARCHIVE="$STAGING_DIR/$(basename "$OUTPUT_PATH")"
SOURCE_DATE_EPOCH="${SOURCE_DATE_EPOCH:-$(git -C "$ROOT_DIR" log -1 --format=%ct)}" \
    python3 "$ROOT_DIR/scripts/create-reproducible-zip.py" \
        --source-root "$PACKAGE_ROOT" \
        --archive "$TEMP_ARCHIVE"
python3 "$ROOT_DIR/scripts/verify-package.py" \
    --archive "$TEMP_ARCHIVE" \
    --publish-root "$PUBLISH_DIR" \
    --staged-root "$PACKAGE_ROOT" \
    --rid "$RID"
mv -f "$TEMP_ARCHIVE" "$OUTPUT_PATH"

if [[ -n "$EXPECTED_MACOS_ARCHITECTURE" ]]; then
    printf 'Packaged unsigned %s output to %s and %s\n' "$RID" "$APP_OUTPUT" "$OUTPUT_PATH"
else
    printf 'Packaged unsigned %s output to %s\n' "$RID" "$OUTPUT_PATH"
fi
