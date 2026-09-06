#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
PUBLISH_DIR="${2:-}"
OUTPUT_PATH="${3:-}"
OUTPUT_WAS_SUPPLIED=$([[ $# -ge 3 ]] && printf true || printf false)
APPIMAGETOOL="${DVM_APPIMAGETOOL:-/opt/appimagetool/AppRun}"
APPIMAGE_RUNTIME="${DVM_APPIMAGE_RUNTIME:-/opt/appimage-runtime}"
REPRODUCIBLE_EPOCH_FALLBACK=946684800
DESKTOP_ENTRY_ID="org.dvmproject.dvmconsole"
DESKTOP_ENTRY_NAME="$DESKTOP_ENTRY_ID.desktop"

case "$RID" in
    linux-x64)
        APPIMAGE_ARCH=x86_64
        EXPECTED_ARCHITECTURE="x86-64"
        ;;
    linux-arm64)
        APPIMAGE_ARCH=aarch64
        EXPECTED_ARCHITECTURE="ARM aarch64"
        ;;
    *)
        printf 'Usage: %s <linux-x64|linux-arm64> <publish-directory> [output.AppImage]\n' "${0##*/}" >&2
        exit 2
        ;;
esac

if [[ ! -d "$PUBLISH_DIR" ]]; then
    printf 'Publish directory does not exist: %s\n' "$PUBLISH_DIR" >&2
    exit 3
fi
if [[ ! -x "$APPIMAGETOOL" || ! -f "$APPIMAGE_RUNTIME" ]]; then
    printf 'AppImage packaging requires the verified portable builder image.\n' >&2
    exit 3
fi
if [[ -z "$OUTPUT_PATH" ]]; then
    OUTPUT_PATH="$ROOT_DIR/artifacts/DVMConsole-$APPIMAGE_ARCH.AppImage"
fi
if [[ "$OUTPUT_PATH" != *.AppImage ]]; then
    printf 'AppImage output must end in .AppImage: %s\n' "$OUTPUT_PATH" >&2
    exit 2
fi

"$ROOT_DIR/scripts/verify-publish.sh" "$RID" "$PUBLISH_DIR"

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/dvmconsole-appimage.XXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

APP_DIR="$STAGING_DIR/DVMConsole.AppDir"
mkdir -p \
    "$APP_DIR/usr/lib/dvmconsole" \
    "$APP_DIR/usr/share/applications" \
    "$APP_DIR/usr/share/icons/hicolor/1024x1024/apps"
cp -R "$PUBLISH_DIR"/. "$APP_DIR/usr/lib/dvmconsole/"
cp "$ROOT_DIR/packaging/linux/AppRun" "$APP_DIR/AppRun"
cp "$ROOT_DIR/packaging/linux/DvmConsole.desktop" "$APP_DIR/$DESKTOP_ENTRY_NAME"
cp "$ROOT_DIR/packaging/linux/DvmConsole.desktop" \
    "$APP_DIR/usr/share/applications/$DESKTOP_ENTRY_NAME"
cp "$ROOT_DIR/src/DvmConsole.Desktop/Assets/DVMConsole.png" "$APP_DIR/DvmConsole.png"
cp "$ROOT_DIR/src/DvmConsole.Desktop/Assets/DVMConsole.png" \
    "$APP_DIR/usr/share/icons/hicolor/1024x1024/apps/DvmConsole.png"
chmod 755 "$APP_DIR/AppRun" "$APP_DIR/usr/lib/dvmconsole/DvmConsole"

target_arguments=(
    --target "$OUTPUT_PATH"
    --extension .AppImage
    --repository-root "$ROOT_DIR"
    --publish-root "$PUBLISH_DIR"
    --staging-root "$STAGING_DIR"
)
if [[ "$OUTPUT_WAS_SUPPLIED" == false ]]; then
    target_arguments+=(--allow-repository-target)
fi
if ! OUTPUT_PATH="$(python3 "$ROOT_DIR/scripts/validate-package-target.py" "${target_arguments[@]}")"; then
    printf 'Unsafe AppImage output target: %s\n' "${3:-$ROOT_DIR/artifacts/DVMConsole-$APPIMAGE_ARCH.AppImage}" >&2
    exit 2
fi
mkdir -p "$(dirname "$OUTPUT_PATH")"
TEMP_OUTPUT="$STAGING_DIR/$(basename "$OUTPUT_PATH")"
if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
    if command -v git >/dev/null 2>&1 && git -C "$ROOT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        SOURCE_DATE_EPOCH="$(git -C "$ROOT_DIR" log -1 --format=%ct)"
    else
        SOURCE_DATE_EPOCH="$REPRODUCIBLE_EPOCH_FALLBACK"
    fi
fi
if [[ ! "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]] || (( SOURCE_DATE_EPOCH <= 0 )); then
    printf 'SOURCE_DATE_EPOCH must be a positive integer: %s\n' "$SOURCE_DATE_EPOCH" >&2
    exit 2
fi
export SOURCE_DATE_EPOCH
ARCH="$APPIMAGE_ARCH" VERSION="${DVM_RELEASE_VERSION:-${DVM_PACKAGE_VERSION:-preview}}" \
    "$APPIMAGETOOL" \
    --runtime-file "$APPIMAGE_RUNTIME" \
    --comp zstd \
    --mksquashfs-opt -Xcompression-level \
    --mksquashfs-opt 22 \
    --no-appstream \
    "$APP_DIR" \
    "$TEMP_OUTPUT"
chmod 755 "$TEMP_OUTPUT"

description=$(/usr/bin/file "$TEMP_OUTPUT")
if [[ "$description" != *ELF* || "$description" != *"$EXPECTED_ARCHITECTURE"* ]]; then
    printf 'AppImage is not a %s ELF executable: %s\n' "$EXPECTED_ARCHITECTURE" "$description" >&2
    exit 4
fi

mv -f "$TEMP_OUTPUT" "$OUTPUT_PATH"

printf 'Packaged single-file %s AppImage: %s\n' "$RID" "$OUTPUT_PATH"
