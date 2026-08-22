#!/usr/bin/env bash
set -euo pipefail

APP_PATH="${1:-}"
CODEPLUG_PATH="${2:-}"

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
cleanup() {
    rm -f "$RESULT_PATH"
}
trap cleanup EXIT

open -n -W "$APP_PATH" --args --smoke-windows "--smoke-result=$RESULT_PATH" "$CODEPLUG_PATH"

if [[ ! -f "$RESULT_PATH" ]] || [[ "$(head -n 1 "$RESULT_PATH")" != "PASS" ]]; then
    printf 'Desktop window smoke did not report PASS.\n' >&2
    if [[ -f "$RESULT_PATH" ]]; then
        cat "$RESULT_PATH" >&2
    fi
    exit 10
fi

printf 'Desktop window smoke passed: %s\n' "$APP_PATH"
