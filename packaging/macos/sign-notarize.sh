#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# ============================================================================
# Digital Voice Modem - Desktop Dispatch Console (Avalonia Shell)
# AGPLv3 Open Source. Use is subject to license terms.
# DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
#
# sign-notarize.sh - Developer ID sign, notarize, and staple a built .app.
#
# Runs on a macOS machine with:
#   - a Developer ID Application certificate in the login keychain
#   - Xcode Command Line Tools (codesign, notarytool)
#   - notarytool credentials (see --help; either --profile or
#     --apple-id/--team-id/--password, or an App Store Connect API key)
#
# The pipeline (in order, each step verified before the next):
#   1. codesign --deep --force --options runtime --timestamp with the
#      hardened-runtime entitlements (packaging/macos/Entitlements.plist)
#   2. codesign --verify --deep --strict --verbose=2
#   3. notarytool submit + wait (xcrun notarytool)
#   4. xcrun stapler staple + stapler validate
#
# Usage:
#   sign-notarize.sh -a PATH/TO/DvmConsole.app -i "Developer ID Application: Name (TEAMID)" [credentials...]
#
# Credentials (choose one form):
#   --profile NAME                       notarytool keychain profile (preferred; store once via
#                                        `xcrun notarytool store-credentials NAME --apple-id ...`)
#   --apple-id EMAIL --team-id TEAMID --password APP_SPECIFIC_PASSWORD
#   --key-id KEYID --issuer-id ISSUERID --api-key /path/To/AuthKey_XXXX.p8
# ============================================================================

set -euo pipefail

app_path=""
identity=""
cred_args=()
entitlements="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/Entitlements.plist"
dry_run=0

usage() {
    cat <<'EOF'
Usage: sign-notarize.sh -a APP -i IDENTITY [credentials...] [-n] [-h]

Sign, notarize, and staple a macOS .app for distribution.

Required:
  -a APP          path to the built .app bundle (from packaging/macos/build-app.sh)
  -i IDENTITY     codesign identity, e.g. "Developer ID Application: Name (TEAMID)"

Credentials (exactly one form):
  -p PROFILE      notarytool keychain profile name (preferred)
  --apple-id EMAIL --team-id TEAMID --password APP_SPECIFIC_PASSWORD
  --key-id KEYID --issuer-id ISSUERID --api-key PATH_TO.p8

Options:
  -e ENTITLEMENTS  entitlements plist (default: packaging/macos/Entitlements.plist)
  -n               dry-run: print the plan, write nothing
  -h               show this help
EOF
}

die() {
    printf 'sign-notarize.sh: ERROR: %s\n' "$*" >&2
    exit 1
}

# --- Argument parsing ------------------------------------------------------
while [ "$#" -gt 0 ]; do
    case "$1" in
        -a) app_path="${2:-}"; shift 2 ;;
        -i) identity="${2:-}"; shift 2 ;;
        -p) cred_args+=(--profile "${2:-}"); shift 2 ;;
        --apple-id) cred_args+=(--apple-id "${2:-}"); shift 2 ;;
        --team-id) cred_args+=(--team-id "${2:-}"); shift 2 ;;
        --password) cred_args+=(--password "${2:-}"); shift 2 ;;
        --key-id) cred_args+=(--key-id "${2:-}"); shift 2 ;;
        --issuer-id) cred_args+=(--issuer-id "${2:-}"); shift 2 ;;
        --api-key) cred_args+=(--api-key "${2:-}"); shift 2 ;;
        -e) entitlements="${2:-}"; shift 2 ;;
        -n) dry_run=1; shift ;;
        -h) usage; exit 0 ;;
        *) die "unexpected argument: $1" ;;
    esac
done

# --- Validation ------------------------------------------------------------
[ -n "${app_path}" ]  || die "missing required -a APP"
[ -n "${identity}" ]  || die "missing required -i IDENTITY"
[ -d "${app_path}" ]  || die "app bundle not found: ${app_path}"
case "${app_path}" in
    *.app) ;;
    *) die "app path must end in .app: ${app_path}" ;;
esac
[ -f "${entitlements}" ] || die "entitlements not found: ${entitlements}"

# Credential form sanity: need exactly one of profile / apple-id+team-id+password / key trio.
if [ "${#cred_args[@]}" -eq 0 ]; then
    die "no notarytool credentials given (see -h)"
fi

codesign_bin="$(command -v codesign || true)"
notarytool_bin="$(command -v notarytool || true)"
stapler_bin="$(command -v stapler || true)"
if [ -z "${codesign_bin}" ]; then
    die "codesign not found (install Xcode Command Line Tools)"
fi
if [ -z "${notarytool_bin}" ] || [ -z "${stapler_bin}" ]; then
    die "notarytool/stapler not found (install Xcode Command Line Tools)"
fi

# --- Plan ------------------------------------------------------------------
printf 'APP           : %s\n' "${app_path}"
printf 'IDENTITY      : %s\n' "${identity}"
printf 'ENTITLEMENTS  : %s\n' "${entitlements}"
printf 'NOTARYTOOL    : %s\n' "${cred_args[*]}"

if [ "${dry_run}" -eq 1 ]; then
    printf 'DRY-RUN: validation passed, nothing was written.\n'
    exit 0
fi

# --- 1. Sign ----------------------------------------------------------------
printf '\n[1/4] codesign (deep, hardened runtime, timestamped)...\n'
codesign --force --deep --sign "${identity}" \
    --options runtime \
    --timestamp \
    --entitlements "${entitlements}" \
    "${app_path}"

# --- 2. Verify --------------------------------------------------------------
printf '\n[2/4] codesign --verify (strict, deep)...\n'
codesign --verify --deep --strict --verbose=2 "${app_path}" 2>&1 \
    | sed 's/^/    /'

# --- 3. Notarize ------------------------------------------------------------
printf '\n[3/4] notarytool submit + wait...\n'
submission="$(xcrun notarytool submit "${app_path}" "${cred_args[@]}" \
    --wait --output-format json)"
printf '%s\n' "${submission}" | sed 's/^/    /'
status="$(printf '%s' "${submission}" | python3 -c \
    'import json,sys; d=json.load(sys.stdin); print(d.get("status", ""))' 2>/dev/null || true)"
if [ -n "${status}" ] && [ "${status}" != "Accepted" ]; then
    die "notarization failed with status: ${status}"
fi

# --- 4. Staple --------------------------------------------------------------
printf '\n[4/4] stapler staple + validate...\n'
xcrun stapler staple "${app_path}" | sed 's/^/    /'
xcrun stapler validate "${app_path}" | sed 's/^/    /'

printf '\nDONE: %s is signed, notarized, and stapled.\n' "${app_path}"
