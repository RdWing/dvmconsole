#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PUBLISH_DIR="${2:-}"
OUTPUT_PATH="${3:-$ROOT_DIR/artifacts/dvmconsole-$RID.zip}"
APP_OUTPUT="${4:-}"

if [[ -z "$RID" || -z "$PUBLISH_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|osx-x64|win-x64> <publish-directory> [zip-output] [macos-app-output]\n' "${0##*/}" >&2
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

if [[ ! -d "$PUBLISH_DIR" ]]; then
    printf 'Publish directory does not exist: %s\n' "$PUBLISH_DIR" >&2
    exit 3
fi

"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$PUBLISH_DIR"

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/dvmconsole-package.XXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

mkdir -p "$(dirname "$OUTPUT_PATH")"

OUTPUT_PATH="$(cd "$(dirname "$OUTPUT_PATH")" && pwd)/$(basename "$OUTPUT_PATH")"

if [[ -n "$EXPECTED_MACOS_ARCHITECTURE" ]]; then
    if [[ -z "$APP_OUTPUT" ]]; then
        APP_OUTPUT="$(dirname "$OUTPUT_PATH")/DVMConsole.app"
    else
        mkdir -p "$(dirname "$APP_OUTPUT")"
        APP_OUTPUT="$(cd "$(dirname "$APP_OUTPUT")" && pwd)/$(basename "$APP_OUTPUT")"
    fi

    APP_PATH="$STAGING_DIR/DVMConsole.app"
    mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources"
    cp "$ROOT_DIR/packaging/macos/Info.plist" "$APP_PATH/Contents/Info.plist"
    if [[ -n "${DVM_RELEASE_VERSION:-}" ]]; then
        if [[ ! "$DVM_RELEASE_VERSION" =~ ^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$ ]]; then
            printf 'DVM_RELEASE_VERSION is not a valid bundle version: %s\n' "$DVM_RELEASE_VERSION" >&2
            exit 12
        fi
        /usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $DVM_RELEASE_VERSION" "$APP_PATH/Contents/Info.plist"
        bundle_version="${DVM_RELEASE_VERSION%%-*}"
        /usr/libexec/PlistBuddy -c "Set :CFBundleVersion $bundle_version" "$APP_PATH/Contents/Info.plist"
    fi
    cp "$ROOT_DIR/packaging/macos/DVMConsole.icns" "$APP_PATH/Contents/Resources/DVMConsole.icns"
    plutil -lint "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c 'Print :NSLocalNetworkUsageDescription' "$APP_PATH/Contents/Info.plist" >/dev/null
    bundle_minimum_version=$(/usr/libexec/PlistBuddy -c 'Print :LSMinimumSystemVersion' "$APP_PATH/Contents/Info.plist")
    if [[ "$bundle_minimum_version" != "14.0" ]]; then
        printf 'macOS bundle minimum version is not 14.0: %s\n' "$bundle_minimum_version" >&2
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
    rm -rf "$APP_OUTPUT"
    cp -R "$APP_PATH" "$APP_OUTPUT"
    PACKAGE_ROOT="$APP_PATH"
else
    PACKAGE_ROOT="$STAGING_DIR/DVMConsole-$RID"
    mkdir -p "$PACKAGE_ROOT"
    cp -R "$PUBLISH_DIR"/. "$PACKAGE_ROOT/"
fi

rm -f "$OUTPUT_PATH"
(
    cd "$STAGING_DIR"
    zip -q -r "$OUTPUT_PATH" "$(basename "$PACKAGE_ROOT")"
)

if [[ -n "$EXPECTED_MACOS_ARCHITECTURE" ]]; then
    printf 'Packaged unsigned %s output to %s and %s\n' "$RID" "$APP_OUTPUT" "$OUTPUT_PATH"
else
    printf 'Packaged unsigned %s output to %s\n' "$RID" "$OUTPUT_PATH"
fi
