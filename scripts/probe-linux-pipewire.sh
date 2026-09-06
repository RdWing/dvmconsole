#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
LIBRARY_PATH="${1:?Usage: probe-linux-pipewire.sh /path/to/libdvmaudio-pipewire.so}"
PROBE_RUNTIME_DIR="$(mktemp -d)"
PIPEWIRE_LOG="$PROBE_RUNTIME_DIR/pipewire.log"
PIPEWIRE_PID=""

cleanup() {
    if [[ -n "$PIPEWIRE_PID" ]]; then
        kill "$PIPEWIRE_PID" 2>/dev/null || true
        wait "$PIPEWIRE_PID" 2>/dev/null || true
    fi
    rm -rf "$PROBE_RUNTIME_DIR"
}
trap cleanup EXIT

export XDG_RUNTIME_DIR="$PROBE_RUNTIME_DIR"
pipewire >"$PIPEWIRE_LOG" 2>&1 &
PIPEWIRE_PID="$!"

for _ in {1..100}; do
    if [[ -S "$XDG_RUNTIME_DIR/pipewire-0" ]]; then
        break
    fi
    if ! kill -0 "$PIPEWIRE_PID" 2>/dev/null; then
        cat "$PIPEWIRE_LOG" >&2
        exit 1
    fi
    sleep 0.05
done
if [[ ! -S "$XDG_RUNTIME_DIR/pipewire-0" ]]; then
    cat "$PIPEWIRE_LOG" >&2
    printf 'PipeWire did not create its runtime socket.\n' >&2
    exit 1
fi

DVM_PIPEWIRE_AUDIO_LIBRARY="$LIBRARY_PATH" \
    dotnet run \
        --project "$ROOT_DIR/src/DvmConsole.AudioProbe/DvmConsole.AudioProbe.csproj" \
        --configuration Release \
        --no-restore \
        -- --linux-devices "$LIBRARY_PATH"
