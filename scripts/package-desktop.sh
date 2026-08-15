#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PUBLISH_DIR="${2:-}"
OUTPUT_PATH="${3:-$ROOT_DIR/artifacts/dvmconsole-$RID.zip}"

if [[ -z "$RID" || -z "$PUBLISH_DIR" ]]; then
    printf 'Usage: %s <osx-arm64|win-x64> <publish-directory> [zip-or-app-output]\n' "${0##*/}" >&2
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

if [[ "$RID" == "osx-arm64" ]]; then
    APP_PATH="$STAGING_DIR/DVMConsole.app"
    mkdir -p "$APP_PATH/Contents/MacOS" "$APP_PATH/Contents/Resources/publish"
    cp "$ROOT_DIR/packaging/macos/Info.plist" "$APP_PATH/Contents/Info.plist"
    cp "$ROOT_DIR/packaging/macos/DvmConsoleLauncher" "$APP_PATH/Contents/MacOS/DvmConsoleLauncher"
    chmod 755 "$APP_PATH/Contents/MacOS/DvmConsoleLauncher"
    cp -R "$PUBLISH_DIR"/. "$APP_PATH/Contents/Resources/publish/"
    PACKAGE_ROOT="$APP_PATH"
else
    PACKAGE_ROOT="$STAGING_DIR/DVMConsole-$RID"
    mkdir -p "$PACKAGE_ROOT"
    cp -R "$PUBLISH_DIR"/. "$PACKAGE_ROOT/"
fi

rm -f "$OUTPUT_PATH"
(
    cd "$STAGING_DIR"
    if [[ "$RID" == "osx-arm64" ]]; then
        zip -q -r "$OUTPUT_PATH" "$(basename "$PACKAGE_ROOT")"
    else
        zip -q -r "$OUTPUT_PATH" "$(basename "$PACKAGE_ROOT")"
    fi
)

printf 'Packaged unsigned %s output to %s\n' "$RID" "$OUTPUT_PATH"
