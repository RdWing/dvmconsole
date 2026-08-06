#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# ============================================================================
# Digital Voice Modem - Desktop Dispatch Console (Avalonia Shell)
# AGPLv3 Open Source. Use is subject to license terms.
# DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
#
# build-vocoder.sh - Reproducible macOS build gate for the native dvmvocoder
# library (libvocoder.dylib).
#
# The upstream source is pinned in dvmvocoder.lock (URL + exact commit). This
# script obtains exactly that commit, configures and builds it with CMake for
# arm64 and x86_64 (Release, macOS deployment target 12.0 by default), and
# validates every output: Mach-O dylib, expected architecture, and all eight
# MBE encoder/decoder exports. It never signs or notarizes, and it never
# downloads anything other than the pinned repository.
#
# Outputs land under <out-root>/osx-arm64 and <out-root>/osx-x64 (default
# out-root: <repo>/artifacts/vocoder, which is outside Git).
# ============================================================================

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
lock_file="${script_dir}/dvmvocoder.lock"

# --- Defaults (overridable via flags or environment) ------------------------
out_root="${DVM_VOCODER_OUT_ROOT:-${repo_root}/artifacts/vocoder}"
cmake_bin="${DVM_CMAKE:-cmake}"
deployment_target="${DVM_MACOSX_DEPLOYMENT_TARGET:-12.0}"
arch_list="${DVM_VOCODER_ARCHES:-arm64,x86_64}"
file_bin="${DVM_FILE:-file}"
nm_bin="${DVM_NM:-nm}"

source_dir=""
dry_run=0

# The eight MBE exports the managed vocoder interop depends on
# (extern "C" in upstream MBEEncoder.h / MBEDecoder.h; _-prefixed on macOS).
EXPORTS=(
    MBEEncoder_Create MBEEncoder_Encode MBEEncoder_EncodeBits MBEEncoder_Delete
    MBEDecoder_Create MBEDecoder_Decode MBEDecoder_DecodeBits MBEDecoder_Delete
)

die()  { printf 'build-vocoder.sh: ERROR: %s\n' "$*" >&2; exit 1; }
warn() { printf 'build-vocoder.sh: warning: %s\n' "$*" >&2; }

arch_dir() {
    case "$1" in
        arm64)  printf 'osx-arm64' ;;
        x86_64) printf 'osx-x64' ;;
        *) die "unsupported architecture: $1" ;;
    esac
}

usage() {
    cat <<'EOF'
Usage: build-vocoder.sh [options]

Reproducible macOS build gate for the pinned upstream dvmvocoder library.
Produces unsigned libvocoder.dylib for arm64 and x86_64 (Release,
MACOSX_DEPLOYMENT_TARGET 12.0 by default) under <out-root>/osx-arm64 and
<out-root>/osx-x64, validating Mach-O type, architecture, and all 8 exports.

Options:
  --source-dir DIR        Use an existing checkout (offline/review builds).
                          Must be a clean Git work tree whose HEAD is exactly
                          the pinned commit; it is never mutated.
  --out-root DIR          Output root (default: <repo>/artifacts/vocoder;
                          also DVM_VOCODER_OUT_ROOT).
  --cmake PATH            CMake executable (default: cmake; also DVM_CMAKE).
  --deployment-target VER macOS deployment target, e.g. 12.0 (default: 12.0;
                          also DVM_MACOSX_DEPLOYMENT_TARGET).
  --arches LIST           Comma-separated arches to build: arm64,x86_64
                          (default: both; also DVM_VOCODER_ARCHES).
  --dry-run               Validate the pin and inputs, print the plan,
                          clone/build/write nothing.
  -h, --help              Show this help.

The pinned URL and commit come only from dvmvocoder.lock next to this script.
No codesign, no notarization, and no downloads beyond the pinned repository.
EOF
}

# --- Argument parsing ------------------------------------------------------
while [ "$#" -gt 0 ]; do
    case "$1" in
        --source-dir)        [ "$#" -ge 2 ] || die "--source-dir needs an argument"; source_dir="$2"; shift 2 ;;
        --out-root)          [ "$#" -ge 2 ] || die "--out-root needs an argument"; out_root="$2"; shift 2 ;;
        --cmake)             [ "$#" -ge 2 ] || die "--cmake needs an argument"; cmake_bin="$2"; shift 2 ;;
        --deployment-target) [ "$#" -ge 2 ] || die "--deployment-target needs an argument"; deployment_target="$2"; shift 2 ;;
        --arches)            [ "$#" -ge 2 ] || die "--arches needs an argument"; arch_list="$2"; shift 2 ;;
        --dry-run)           dry_run=1; shift ;;
        -h|--help)           usage; exit 0 ;;
        *) die "unknown option: $1 (see --help)" ;;
    esac
done

# --- Load the pin (parse only; never execute lock content) ------------------
[ -f "${lock_file}" ] || die "pin file not found: ${lock_file}"
lock_url="$(sed -n 's/^DVMVOCODER_URL=//p' "${lock_file}" | tail -n 1 | tr -d '\r')"
lock_commit="$(sed -n 's/^DVMVOCODER_COMMIT=//p' "${lock_file}" | tail -n 1 | tr -d '\r')"
[ -n "${lock_url}" ]    || die "pin file ${lock_file} has no DVMVOCODER_URL line"
[ -n "${lock_commit}" ] || die "pin file ${lock_file} has no DVMVOCODER_COMMIT line"
case "${lock_url}" in
    https://*|git@*|ssh://*|file://*) ;;
    *) die "refusing unpinned/unrecognized DVMVOCODER_URL in ${lock_file}: ${lock_url}" ;;
esac
[[ "${lock_commit}" =~ ^[0-9a-f]{40}$ ]] \
    || die "DVMVOCODER_COMMIT in ${lock_file} is not a 40-hex SHA-1: ${lock_commit}"

# --- Architectures ----------------------------------------------------------
IFS=',' read -r -a arches <<< "${arch_list}"
[ "${#arches[@]}" -gt 0 ] || die "empty --arches"
for a in "${arches[@]}"; do
    case "${a}" in
        arm64|x86_64) ;;
        *) die "unsupported architecture: ${a} (use arm64 and/or x86_64)" ;;
    esac
done

# --- Source acquisition: exactly the pinned commit -------------------------
src=""
if [ -n "${source_dir}" ]; then
    # Offline/review mode: verify only, never mutate the caller's checkout.
    src="$(cd "${source_dir}" && pwd)"
    [ -d "${src}/.git" ] || die "--source-dir is not a git checkout: ${src}"
    [ -z "$(git -C "${src}" status --porcelain)" ] \
        || die "--source-dir has uncommitted changes; refusing a non-deterministic review build: ${src}"
    head_sha="$(git -C "${src}" rev-parse HEAD)"
    [ "${head_sha}" = "${lock_commit}" ] \
        || die "--source-dir HEAD ${head_sha} != pinned commit ${lock_commit} (${src})"
    printf 'SOURCE-DIR (offline/review): %s @ %s\n' "${src}" "${head_sha:0:12}"
else
    src="${out_root}/src"
    if [ "${dry_run}" -eq 0 ]; then
        if [ ! -d "${src}/.git" ]; then
            mkdir -p "$(dirname "${src}")"
            printf 'Cloning %s (pinned by %s)...\n' "${lock_url}" "${lock_file}"
            git clone --no-checkout "${lock_url}" "${src}"
        fi
        if ! git -C "${src}" rev-parse --verify --quiet "${lock_commit}^{commit}" >/dev/null 2>&1; then
            git -C "${src}" fetch --tags --prune origin
        fi
        if ! git -C "${src}" rev-parse --verify --quiet "${lock_commit}^{commit}" >/dev/null 2>&1; then
            git -C "${src}" fetch origin "${lock_commit}" || true
        fi
        git -C "${src}" rev-parse --verify --quiet "${lock_commit}^{commit}" >/dev/null 2>&1 \
            || die "pinned commit ${lock_commit} not reachable from ${lock_url} (fetch failed)"
        git -C "${src}" -c advice.detachedHead=false checkout --quiet --detach "${lock_commit}"
        head_sha="$(git -C "${src}" rev-parse HEAD)"
        [ "${head_sha}" = "${lock_commit}" ] || die "checked-out HEAD ${head_sha} != pinned commit ${lock_commit}"
        printf 'SOURCE: %s @ %s\n' "${src}" "${head_sha:0:12}"
    fi
fi

# --- CMake sanity -----------------------------------------------------------
command -v "${cmake_bin}" >/dev/null 2>&1 \
    || die "CMake not found: ${cmake_bin} (install CMake >= 3.16 or pass --cmake PATH)"
cmake_ver="$("${cmake_bin}" --version | head -n 1 | sed 's/[^0-9.]*//g')"
cM="${cmake_ver%%.*}"; cN="${cmake_ver#*.}"; cN="${cN%%.*}"
{ [ "${cM}" -gt 3 ] || { [ "${cM}" -eq 3 ] && [ "${cN}" -ge 16 ]; }; } \
    || die "CMake ${cmake_ver} is too old; upstream dvmvocoder requires CMake >= 3.16"

if [ "$(uname -s)" != "Darwin" ]; then
    warn "not running on macOS; native builds need an Apple toolchain (a --dry-run or stubbed run is fine)"
fi

# --- Plan -------------------------------------------------------------------
plan() {
    printf 'LOCK_FILE        : %s\n' "${lock_file}"
    printf 'UPSTREAM_URL     : %s\n' "${lock_url}"
    printf 'PINNED_COMMIT    : %s\n' "${lock_commit}"
    printf 'SOURCE           : %s\n' "${src}"
    printf 'OUT_ROOT         : %s\n' "${out_root}"
    printf 'CMAKE            : %s (%s)\n' "${cmake_bin}" "${cmake_ver}"
    printf 'DEPLOYMENT_TARGET: %s\n' "${deployment_target}"
    printf 'ARCHES           : %s\n' "${arch_list}"
    for a in "${arches[@]}"; do
        printf 'OUTPUT           : %s/libvocoder.dylib (%s)\n' "${out_root}/$(arch_dir "${a}")" "${a}"
    done
}
plan
if [ "${dry_run}" -eq 1 ]; then
    printf 'DRY-RUN: validation passed, nothing was cloned or written.\n'
    exit 0
fi

# --- Build + validate one architecture --------------------------------------
validate_dylib() {
    local dylib="$1" want_arch="$2" desc=""
    [ -f "${dylib}" ] || die "missing output: ${dylib}"
    if command -v "${file_bin}" >/dev/null 2>&1; then
        desc="$("${file_bin}" -b "${dylib}")"
        case "${desc}" in
            *"Mach-O"*) ;;
            *) die "not a Mach-O binary: ${dylib}: ${desc}" ;;
        esac
        case "${desc}" in
            *"dynamically linked shared library"*) ;;
            *) die "not a shared library (dylib): ${dylib}: ${desc}" ;;
        esac
        case "${desc}" in
            *"${want_arch}"*) ;;
            *) die "architecture mismatch for ${dylib}: expected ${want_arch}, file reports: ${desc}" ;;
        esac
    else
        die "cannot verify ${dylib}: '${file_bin}' not available; refusing to accept an unverified architecture"
    fi
    command -v "${nm_bin}" >/dev/null 2>&1 \
        || die "cannot verify ${dylib}: '${nm_bin}' not available"
    for e in "${EXPORTS[@]}"; do
        if ! "${nm_bin}" -gU "${dylib}" 2>/dev/null | awk '{print $NF}' | grep -qx "_${e}"; then
            die "missing exported symbol ${e} in ${dylib}"
        fi
    done
    if command -v otool >/dev/null 2>&1; then
        printf 'INSTALL NAME     : %s\n' "$(otool -D "${dylib}" 2>/dev/null | tail -n 1)"
    fi
    printf 'VALID            : %s (%s, Mach-O dylib, 8/8 exports)\n' "${dylib}" "${want_arch}"
}

for a in "${arches[@]}"; do
    dir="$(arch_dir "${a}")"
    out_dir="${out_root}/${dir}"
    build_dir="${out_root}/build/${dir}"
    printf '== Building %s (%s) ==\n' "${a}" "${dir}"
    mkdir -p "${out_dir}" "${build_dir}"
    "${cmake_bin}" -S "${src}" -B "${build_dir}" \
        -DCMAKE_BUILD_TYPE=Release \
        -DCMAKE_OSX_ARCHITECTURES="${a}" \
        -DCMAKE_OSX_DEPLOYMENT_TARGET="${deployment_target}" \
        -DCMAKE_OSX_SYSROOT=macosx
    "${cmake_bin}" --build "${build_dir}" --config Release --parallel
    cp "${build_dir}/libvocoder.dylib" "${out_dir}/libvocoder.dylib"
    validate_dylib "${out_dir}/libvocoder.dylib" "${a}"
done

# --- Summary ----------------------------------------------------------------
printf '\nBuilt UNSIGNED libvocoder.dylib (not signed, not notarized):\n'
for a in "${arches[@]}"; do
    printf '  %s/libvocoder.dylib\n' "${out_root}/$(arch_dir "${a}")"
done
printf 'Bundle it with: packaging/macos/build-app.sh -p <publish> -o dist/DvmConsole.app -v <one of the above>\n'
