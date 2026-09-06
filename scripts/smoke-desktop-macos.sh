#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

APP_PATH="${1:-}"
CODEPLUG_PATH="${2:-}"
LOG_PATH="${3:-${RUNNER_TEMP:-${TMPDIR:-/tmp}}/dvmconsole-macos-smoke.log}"

if [[ -z "$APP_PATH" || -z "$CODEPLUG_PATH" ]]; then
    printf 'Usage: %s <DVMConsole.app> <codeplug.yml>\n' "${0##*/}" >&2
    exit 2
fi
if [[ ! -d "$APP_PATH" ]]; then
    printf 'Application bundle does not exist: %s\n' "$APP_PATH" >&2
    exit 3
fi
if [[ ! -f "$CODEPLUG_PATH" ]]; then
    printf 'Codeplug does not exist: %s\n' "$CODEPLUG_PATH" >&2
    exit 3
fi

APP_PATH="$(cd "$(dirname "$APP_PATH")" && pwd)/$(basename "$APP_PATH")"
CODEPLUG_PATH="$(cd "$(dirname "$CODEPLUG_PATH")" && pwd)/$(basename "$CODEPLUG_PATH")"
RESULT_PATH="$(mktemp "${TMPDIR:-/tmp}/dvmconsole-smoke.XXXXXX")"
OPEN_PID=""
cleanup() {
    rm -f "$RESULT_PATH"
    if [[ -n "$OPEN_PID" ]] && kill -0 "$OPEN_PID" 2>/dev/null; then
        kill "$OPEN_PID" 2>/dev/null || true
    fi
}
trap cleanup EXIT

rm -f "$LOG_PATH"
open -n -W "$APP_PATH" --args --demo --smoke-windows "--smoke-result=$RESULT_PATH" "$CODEPLUG_PATH" >"$LOG_PATH" 2>&1 &
OPEN_PID=$!

EXECUTABLE_NAME="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_PATH/Contents/Info.plist")"
EXECUTABLE_PATH="$APP_PATH/Contents/MacOS/$EXECUTABLE_NAME"
deadline=$((SECONDS + 120))
while kill -0 "$OPEN_PID" 2>/dev/null; do
    if ((SECONDS >= deadline)); then
        pkill -TERM -f "$EXECUTABLE_PATH" 2>/dev/null || true
        sleep 1
        pkill -KILL -f "$EXECUTABLE_PATH" 2>/dev/null || true
        kill -KILL "$OPEN_PID" 2>/dev/null || true
        printf 'Desktop window smoke exceeded the 120 second watchdog.\n' >&2
        sed -n '1,240p' "$LOG_PATH" >&2
        exit 12
    fi
    sleep 0.2
done
wait "$OPEN_PID"
OPEN_PID=""

for _ in {1..50}; do
    if ! pgrep -f "$EXECUTABLE_PATH" >/dev/null; then
        break
    fi
    sleep 0.1
done
if pgrep -f "$EXECUTABLE_PATH" >/dev/null; then
    printf 'Desktop window smoke left its application process running: %s\n' "$EXECUTABLE_PATH" >&2
    sed -n '1,240p' "$LOG_PATH" >&2
    exit 11
fi

if [[ ! -f "$RESULT_PATH" ]] || [[ "$(head -n 1 "$RESULT_PATH")" != "PASS" ]]; then
    printf 'Desktop window smoke did not report PASS.\n' >&2
    if [[ -f "$RESULT_PATH" ]]; then
        cat "$RESULT_PATH" >&2
    fi
    sed -n '1,240p' "$LOG_PATH" >&2
    exit 10
fi

printf 'Desktop window smoke passed: %s\n' "$APP_PATH"
