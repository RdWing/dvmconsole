#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PUBLISH_DIR="${2:-}"
OUTPUT_PATH="${3:-$ROOT_DIR/artifacts/dvmconsole-$RID.zip}"
APP_OUTPUT="${4:-}"

if [[ -z "$RID" || -z "$PUBLISH_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|win-x64> <publish-directory> [zip-output] [macos-app-output]\n' "${0##*/}" >&2
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

if [[ "$RID" == "osx-arm64" ]]; then
    if [[ -z "$APP_OUTPUT" ]]; then
        APP_OUTPUT="$(dirname "$OUTPUT_PATH")/DVMConsole.app"
    else
        mkdir -p "$(dirname "$APP_OUTPUT")"
        APP_OUTPUT="$(cd "$(dirname "$APP_OUTPUT")" && pwd)/$(basename "$APP_OUTPUT")"
    fi

    APP_PATH="$STAGING_DIR/DVMConsole.app"
    mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources/publish"
    cp "$ROOT_DIR/packaging/macos/Info.plist" "$APP_PATH/Contents/Info.plist"
    plutil -lint "$APP_PATH/Contents/Info.plist" >/dev/null
    /usr/libexec/PlistBuddy -c 'Print :NSMicrophoneUsageDescription' "$APP_PATH/Contents/Info.plist" >/dev/null
    cp "$ROOT_DIR/packaging/macos/DvmConsoleLauncher" "$APP_PATH/Contents/MacOS/DvmConsoleLauncher"
    chmod 755 "$APP_PATH/Contents/MacOS/DvmConsoleLauncher"
    cp -R "$PUBLISH_DIR"/. "$APP_PATH/Contents/Resources/publish/"
    chmod 755 "$APP_PATH/Contents/Resources/publish/DvmConsole.Desktop"
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

if [[ "$RID" == "osx-arm64" ]]; then
    printf 'Packaged unsigned %s output to %s and %s\n' "$RID" "$APP_OUTPUT" "$OUTPUT_PATH"
else
    printf 'Packaged unsigned %s output to %s\n' "$RID" "$OUTPUT_PATH"
fi
