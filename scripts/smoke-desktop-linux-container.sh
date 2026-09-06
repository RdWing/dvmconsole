#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PACKAGE_PATH="${1:-}"
RID="${2:-linux-x64}"
IMAGE="${3:-}"
PACKAGE_FAMILY="${4:-}"

if [[ -z "$PACKAGE_PATH" || -z "$IMAGE" || -z "$PACKAGE_FAMILY" ]]; then
    printf 'Usage: %s <package.AppImage> <linux-x64|linux-arm64> <container-image> <apt|dnf>\n' "${0##*/}" >&2
    exit 2
fi
case "$RID" in
    linux-x64|linux-arm64) ;;
    *)
        printf 'Supported runtime identifiers: linux-x64, linux-arm64\n' >&2
        exit 2
        ;;
esac
case "$PACKAGE_FAMILY" in
    apt|dnf) ;;
    *)
        printf 'Supported container package families: apt, dnf\n' >&2
        exit 2
        ;;
esac
if [[ ! -f "$PACKAGE_PATH" ]]; then
    printf 'Linux package does not exist: %s\n' "$PACKAGE_PATH" >&2
    exit 3
fi
if ! command -v docker >/dev/null 2>&1; then
    printf 'Container smoke testing requires Docker.\n' >&2
    exit 3
fi

PACKAGE_DIRECTORY="$(cd "$(dirname "$PACKAGE_PATH")" && pwd)"
PACKAGE_NAME="$(basename "$PACKAGE_PATH")"

docker run --rm \
    --env DEBIAN_FRONTEND=noninteractive \
    --volume "$ROOT_DIR:/work:ro" \
    --volume "$PACKAGE_DIRECTORY:/package:ro" \
    "$IMAGE" \
    /bin/bash -c '
        set -euo pipefail
        case "$1" in
            apt)
                apt-get update >/dev/null
                icu_package="$(
                    apt-cache search --names-only "^libicu[0-9][0-9]*$" |
                        head -n 1 |
                        cut -d " " -f 1
                )"
                if [[ -z "$icu_package" ]]; then
                    printf "The container repository does not provide a versioned ICU runtime.\n" >&2
                    exit 4
                fi
                apt-get install --yes --no-install-recommends \
                    ca-certificates \
                    fonts-dejavu-core \
                    libfontconfig1 \
                    libgl1 \
                    libice6 \
                    "$icu_package" \
                    libpipewire-0.3-0 \
                    libsm6 \
                    libx11-6 \
                    libxcursor1 \
                    libxext6 \
                    libxi6 \
                    libxrandr2 \
                    libxrender1 \
                    libxtst6 \
                    xauth \
                    xvfb >/dev/null
                ;;
            dnf)
                dnf install --assumeyes --quiet --setopt=install_weak_deps=False \
                    ca-certificates \
                    dejavu-sans-fonts \
                    fontconfig \
                    libICE \
                    libSM \
                    libX11 \
                    libXcursor \
                    libXext \
                    libXi \
                    libXrandr \
                    libXrender \
                    libXtst \
                    libicu \
                    mesa-libGL \
                    pipewire-libs \
                    xorg-x11-server-Xvfb \
                    xorg-x11-xauth >/dev/null
                ;;
        esac

        . /etc/os-release
        printf "Container smoke baseline: %s %s (%s, %s)\n" \
            "$ID" "$VERSION_ID" "$(uname -m)" "$(getconf GNU_LIBC_VERSION)"
        /work/scripts/smoke-desktop-linux-appimage.sh "/package/$2" "$3"
    ' dvmconsole-linux-smoke "$PACKAGE_FAMILY" "$PACKAGE_NAME" "$RID"
