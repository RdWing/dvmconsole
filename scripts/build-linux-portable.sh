#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="${1:-}"
ARTIFACT_ROOT="${2:-$ROOT_DIR/artifacts}"
ARTIFACT_ROOT_WAS_SUPPLIED=$([[ $# -ge 2 ]] && printf true || printf false)
case "${1:-}" in
    linux-x64) DEFAULT_PACKAGE_NAME="DVMConsole-x86_64.AppImage" ;;
    linux-arm64) DEFAULT_PACKAGE_NAME="DVMConsole-aarch64.AppImage" ;;
    *) DEFAULT_PACKAGE_NAME="" ;;
esac
PACKAGE_NAME="${3:-$DEFAULT_PACKAGE_NAME}"
CONFIGURATION="${CONFIGURATION:-Release}"
BUILDER_IMAGE="${DVM_LINUX_BUILDER_IMAGE:-dvmconsole-linux-builder:bookworm}"
REUSE_BUILDER="${DVM_LINUX_REUSE_BUILDER:-0}"
CONTAINER_USER="${DVM_LINUX_CONTAINER_USER:-$(id -u):$(id -g)}"
REPRODUCIBLE_EPOCH_FALLBACK=946684800

case "$RID" in
    linux-x64) BUILDER_ARCH=amd64 ;;
    linux-arm64) BUILDER_ARCH=arm64 ;;
    *)
        printf 'Usage: %s <linux-x64|linux-arm64> [artifact-directory] [package-filename]\n' "${0##*/}" >&2
        exit 2
        ;;
esac

if [[ "$PACKAGE_NAME" == */* || "$PACKAGE_NAME" != *.AppImage ]]; then
    printf 'Linux package filename must be an AppImage basename: %s\n' "$PACKAGE_NAME" >&2
    exit 2
fi
if ! command -v docker >/dev/null 2>&1; then
    printf 'Portable Linux packaging requires Docker.\n' >&2
    exit 3
fi

if [[ -z "${SOURCE_DATE_EPOCH:-}" ]]; then
    if command -v git >/dev/null 2>&1 && git -C "$ROOT_DIR" rev-parse --is-inside-work-tree >/dev/null 2>&1; then
        SOURCE_DATE_EPOCH="$(git -C "$ROOT_DIR" log -1 --format=%ct)"
    else
        # Exported source archives intentionally have no .git directory. Use a
        # stable epoch rather than making AppImage output depend on wall time.
        SOURCE_DATE_EPOCH="$REPRODUCIBLE_EPOCH_FALLBACK"
    fi
fi
if [[ ! "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]] || (( SOURCE_DATE_EPOCH <= 0 )); then
    printf 'SOURCE_DATE_EPOCH must be a positive integer: %s\n' "$SOURCE_DATE_EPOCH" >&2
    exit 2
fi

target_arguments=(
    --target "$ARTIFACT_ROOT"
    --directory
    --repository-root "$ROOT_DIR"
)
if [[ "$ARTIFACT_ROOT_WAS_SUPPLIED" == false ]]; then
    target_arguments+=(--allow-repository-target)
fi
if ! ARTIFACT_ROOT="$(python3 "$ROOT_DIR/scripts/validate-package-target.py" "${target_arguments[@]}")"; then
    printf 'Unsafe Linux artifact directory: %s\n' "${2:-$ROOT_DIR/artifacts}" >&2
    exit 2
fi
mkdir -p "$ARTIFACT_ROOT"

# The RID directory is generated output owned by this build. Recreate it so a
# prior packaging layout cannot leak stale native libraries into verification.
rm -rf "$ARTIFACT_ROOT/$RID"

if [[ "$REUSE_BUILDER" == "1" ]]; then
    if ! docker image inspect "$BUILDER_IMAGE" >/dev/null 2>&1; then
        printf 'Requested Linux builder image does not exist: %s\n' "$BUILDER_IMAGE" >&2
        exit 3
    fi
else
    docker build \
        --platform "linux/$BUILDER_ARCH" \
        --pull \
        --build-arg "TARGETARCH=$BUILDER_ARCH" \
        --tag "$BUILDER_IMAGE" \
        "$ROOT_DIR/packaging/linux"
fi

actual_builder_arch="$(docker image inspect --format '{{.Architecture}}' "$BUILDER_IMAGE")"
if [[ "$actual_builder_arch" != "$BUILDER_ARCH" ]]; then
    printf 'Linux builder architecture is %s; %s requires %s. Rebuild the matching image.\n' \
        "$actual_builder_arch" "$RID" "$BUILDER_ARCH" >&2
    exit 3
fi

VERSION_ENVIRONMENT=(--env "SOURCE_DATE_EPOCH=$SOURCE_DATE_EPOCH")
if [[ -n "${DVM_RELEASE_VERSION:-}" ]]; then
    VERSION_ENVIRONMENT+=(--env "DVM_RELEASE_VERSION=$DVM_RELEASE_VERSION")
fi
if [[ -n "${DVM_PACKAGE_VERSION:-}" ]]; then
    VERSION_ENVIRONMENT+=(--env "DVM_PACKAGE_VERSION=$DVM_PACKAGE_VERSION")
fi

docker run --rm \
    --platform "linux/$BUILDER_ARCH" \
    --user "$CONTAINER_USER" \
    --env "CONFIGURATION=$CONFIGURATION" \
    --env HOME=/tmp/dvmconsole-home \
    --env CARGO_HOME=/tmp/dvmconsole-cargo \
    --env CARGO_TARGET_DIR=/tmp/dvmconsole-native-target \
    --env DVM_AUDIO_BUILD_DIR=/tmp/dvmconsole-pipewire-build \
    --env RUSTUP_HOME=/opt/rustup \
    "${VERSION_ENVIRONMENT[@]}" \
    --volume "$ROOT_DIR:/work" \
    --volume "$ARTIFACT_ROOT:/artifacts" \
    --workdir /work \
    "$BUILDER_IMAGE" \
    /bin/bash -c '
        set -euo pipefail
        mkdir -p "$HOME" "$CARGO_HOME"
        scripts/publish-desktop.sh "$1" "/artifacts/$1"
        scripts/package-desktop-linux-appimage.sh \
            "$1" \
            "/artifacts/$1" \
            "/artifacts/$2"
        scripts/smoke-desktop-linux-appimage.sh "/artifacts/$2" "$1"
    ' dvmconsole-linux-build "$RID" "$PACKAGE_NAME"

printf 'Portable single-file Linux AppImage built from the Debian 12 baseline: %s\n' \
    "$ARTIFACT_ROOT/$PACKAGE_NAME"
