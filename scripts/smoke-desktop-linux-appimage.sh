#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

APPIMAGE_PATH="${1:-}"
RID="${2:-linux-x64}"

case "$RID" in
    linux-x64|linux-arm64) ;;
    *)
        printf 'Usage: %s <package.AppImage> <linux-x64|linux-arm64>\n' "${0##*/}" >&2
        exit 2
        ;;
esac
if [[ ! -x "$APPIMAGE_PATH" ]]; then
    printf 'Linux AppImage is missing or not executable: %s\n' "$APPIMAGE_PATH" >&2
    exit 3
fi
APPIMAGE_PATH="$(cd "$(dirname "$APPIMAGE_PATH")" && pwd)/$(basename "$APPIMAGE_PATH")"
for command_name in xvfb-run timeout; do
    if ! command -v "$command_name" >/dev/null 2>&1; then
        printf 'Linux AppImage smoke requires %s.\n' "$command_name" >&2
        exit 3
    fi
done

STAGING_DIR="$(mktemp -d "${TMPDIR:-/tmp}/dvmconsole-appimage-smoke.XXXXXX")"
cleanup() {
    rm -rf "$STAGING_DIR"
}
trap cleanup EXIT

RESULT_PATH="$STAGING_DIR/smoke.result"
LOG_PATH="$STAGING_DIR/smoke.log"

run_smoke() {
    local mode="$1"
    rm -f "$RESULT_PATH" "$LOG_PATH"
    set +e
    if [[ "$mode" == direct ]]; then
        env -u APPIMAGE_EXTRACT_AND_RUN timeout 60s xvfb-run -a "$APPIMAGE_PATH" \
            --demo --smoke-windows "--smoke-result=$RESULT_PATH" >"$LOG_PATH" 2>&1
    else
        APPIMAGE_EXTRACT_AND_RUN=1 timeout 60s xvfb-run -a "$APPIMAGE_PATH" \
            --demo --smoke-windows "--smoke-result=$RESULT_PATH" >"$LOG_PATH" 2>&1
    fi
    local exit_code=$?
    set -e
    if [[ $exit_code -ne 0 ]]; then
        printf 'Linux AppImage %s window smoke failed with exit code %s.\n' "$mode" "$exit_code" >&2
        sed -n '1,240p' "$LOG_PATH" >&2
        return "$exit_code"
    fi
    if [[ ! -f "$RESULT_PATH" || "$(sed -n '1p' "$RESULT_PATH")" != "PASS" ]]; then
        printf 'Linux AppImage %s window smoke did not report PASS.\n' "$mode" >&2
        sed -n '1,240p' "$LOG_PATH" >&2
        return 5
    fi
}

if [[ -r /dev/fuse && -w /dev/fuse ]]; then
    run_smoke direct
fi
run_smoke extracted

printf 'Linux AppImage window smoke passed: %s (%s)\n' "$APPIMAGE_PATH" "$RID"
