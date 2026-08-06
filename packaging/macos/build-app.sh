#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
# ============================================================================
# Digital Voice Modem - Desktop Dispatch Console (Avalonia Shell)
# AGPLv3 Open Source. Use is subject to license terms.
# DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
#
# build-app.sh - Assemble an UNSIGNED macOS .app bundle for the Avalonia
# shell from a `dotnet publish` output directory.
#
# This slice intentionally does NOT sign, notarize, or staple the bundle,
# and it does NOT manufacture a native vocoder library: libvocoder.dylib is
# only copied into Contents/Frameworks when the caller supplies one (see
# README.md for the current limitation). Everything this script writes lands
# inside the output .app path; it never touches the publish directory.
# ============================================================================

set -euo pipefail

# --- Defaults (all overridable; see usage below) ---------------------------
DVM_BUNDLE_IDENTIFIER="${DVM_BUNDLE_IDENTIFIER:-org.dvmproject.dvmconsole}"
DVM_BUNDLE_SHORT_VERSION="${DVM_BUNDLE_SHORT_VERSION:-0.1.0}"
DVM_BUNDLE_VERSION="${DVM_BUNDLE_VERSION:-1}"
DVM_LS_MINIMUM_SYSTEM_VERSION="${DVM_LS_MINIMUM_SYSTEM_VERSION:-12.0}"

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
default_plist="${script_dir}/../../DvmConsole.Avalonia/Platforms/macOS/Info.plist"
default_icon="${script_dir}/../../DvmConsole.Avalonia/Assets/AppIcon.icns"

publish_dir=""
out_app=""
executable_name="DvmConsole.Avalonia"
icon_path=""
vocoder_path="${VOCODER_DYLIB:-}"
plist_src="${DVM_INFO_PLIST:-${default_plist}}"
dry_run=0

usage() {
    cat <<'EOF'
Usage: build-app.sh -p PUBLISH_DIR -o OUTPUT_APP [options]

Assemble an unsigned macOS .app bundle for DvmConsole.Avalonia.

Required:
  -p PUBLISH_DIR   dotnet publish output directory (e.g. bin/Release/net8.0/osx-arm64/publish)
  -o OUTPUT_APP    output .app path (e.g. dist/DvmConsole.app)

Options:
  -e NAME          main executable file name inside PUBLISH_DIR
                   (default: DvmConsole.Avalonia; must match CFBundleExecutable)
  -i ICNS_PATH     AppIcon.icns to install as Contents/Resources/AppIcon.icns.
                   REQUIRED: the file must exist or the script fails.
                   If omitted, the script checks PUBLISH_DIR/AppIcon.icns,
                   PUBLISH_DIR/Assets/AppIcon.icns, then the repo asset;
                   otherwise a warning is printed and the bundle has no icon.
  -v DYLIB_PATH    libvocoder dylib to copy into Contents/Frameworks
                   (also honored via VOCODER_DYLIB env var). Must exist.
  -l PLIST         Info.plist template to use (default: repo Platforms/macOS/Info.plist)
  -n               dry-run: validate inputs and print the plan, write nothing
  -h               show this help

Info.plist substitution tokens (env vars, applied to the installed copy):
  DVM_BUNDLE_IDENTIFIER          (default org.dvmproject.dvmconsole)
  DVM_BUNDLE_SHORT_VERSION       (default 0.1.0)
  DVM_BUNDLE_VERSION             (default 1)
  DVM_LS_MINIMUM_SYSTEM_VERSION  (default 12.0)

The bundle is assembled unsigned and is not notarized.
EOF
}

die() {
    printf 'build-app.sh: ERROR: %s\n' "$*" >&2
    exit 1
}

warn() {
    printf 'build-app.sh: warning: %s\n' "$*" >&2
}

# --- Argument parsing ------------------------------------------------------
while getopts ":p:o:e:i:v:l:nh" opt; do
    case "${opt}" in
        p) publish_dir="${OPTARG}" ;;
        o) out_app="${OPTARG}" ;;
        e) executable_name="${OPTARG}" ;;
        i) icon_path="${OPTARG}" ;;
        v) vocoder_path="${OPTARG}" ;;
        l) plist_src="${OPTARG}" ;;
        n) dry_run=1 ;;
        h) usage; exit 0 ;;
        *) usage >&2; exit 2 ;;
    esac
done
shift $((OPTIND - 1))
[ "$#" -eq 0 ] || die "unexpected positional arguments: $*"

# --- Input validation (runs in dry-run mode too) ---------------------------
[ -n "${publish_dir}" ] || die "missing required -p PUBLISH_DIR (see -h)"
[ -n "${out_app}" ]     || die "missing required -o OUTPUT_APP (see -h)"
[ -d "${publish_dir}" ] || die "publish directory does not exist or is not a directory: ${publish_dir}"
[ -f "${plist_src}" ]   || die "Info.plist template not found: ${plist_src}"

case "${out_app}" in
    *.app) ;;
    *) die "output path must end in .app: ${out_app}" ;;
esac
case "${out_app}" in
    /|.|..|"" ) die "refusing unsafe output path: ${out_app}" ;;
esac

publish_exe="${publish_dir}/${executable_name}"
[ -f "${publish_exe}" ] || die "publish directory has no '${executable_name}' executable (is this a dotnet publish output?): ${publish_exe}"

[ -z "${icon_path}" ] || [ -f "${icon_path}" ] || die "icon requested with -i but not found: ${icon_path}"
[ -z "${vocoder_path}" ] || [ -f "${vocoder_path}" ] || die "vocoder dylib requested (VOCODER_DYLIB/-v) but not found: ${vocoder_path}"

# --- Plan ------------------------------------------------------------------
plan() {
    printf 'PUBLISH_DIR      : %s\n' "${publish_dir}"
    printf 'OUTPUT_APP       : %s\n' "${out_app}"
    printf 'EXECUTABLE       : %s\n' "${executable_name}"
    printf 'INFO_PLIST       : %s\n' "${plist_src}"
    printf 'IDENTIFIER       : %s\n' "${DVM_BUNDLE_IDENTIFIER}"
    printf 'SHORT_VERSION    : %s\n' "${DVM_BUNDLE_SHORT_VERSION}"
    printf 'BUILD_VERSION    : %s\n' "${DVM_BUNDLE_VERSION}"
    printf 'MIN_SYSTEM       : %s\n' "${DVM_LS_MINIMUM_SYSTEM_VERSION}"
    if [ -n "${icon_path}" ]; then
        printf 'ICON             : %s (required)\n' "${icon_path}"
    elif [ -f "${publish_dir}/AppIcon.icns" ]; then
        printf 'ICON             : %s/AppIcon.icns (auto-detected)\n' "${publish_dir}"
    elif [ -f "${publish_dir}/Assets/AppIcon.icns" ]; then
        printf 'ICON             : %s/Assets/AppIcon.icns (auto-detected)\n' "${publish_dir}"
    elif [ -f "${default_icon}" ]; then
        printf 'ICON             : %s (auto-detected)\n' "${default_icon}"
    else
        printf 'ICON             : (none - CFBundleIconFile references AppIcon, bundle will be icon-less)\n'
    fi
    if [ -n "${vocoder_path}" ]; then
        printf 'VOCODER_DYLIB    : %s -> Contents/Frameworks/%s\n' "${vocoder_path}" "$(basename "${vocoder_path}")"
    else
        printf 'VOCODER_DYLIB    : (none - libvocoder.dylib NOT bundled; vocoder features unavailable)\n'
    fi
}

plan
if [ "${dry_run}" -eq 1 ]; then
    printf 'DRY-RUN: validation passed, nothing was written.\n'
    exit 0
fi

# --- Assemble ---------------------------------------------------------------
if [ -e "${out_app}" ]; then
    warn "removing existing output before rebuild: ${out_app}"
    rm -rf "${out_app}"
fi

macos_dir="${out_app}/Contents/MacOS"
resources_dir="${out_app}/Contents/Resources"
frameworks_dir="${out_app}/Contents/Frameworks"
mkdir -p "${macos_dir}" "${resources_dir}" "${frameworks_dir}"

# Copy the published tree (dlls, assets, apphost, ...) preserving modes so
# executables stay executable; cp -Rp is portable across macOS and Linux.
cp -Rp "${publish_dir}/." "${macos_dir}/"
chmod +x "${macos_dir}/${executable_name}"

# Install the Info.plist with token substitution. Values come from the
# environment, so spaces and shell metacharacters in them are safe.
export DVM_BUNDLE_IDENTIFIER DVM_BUNDLE_SHORT_VERSION DVM_BUNDLE_VERSION DVM_LS_MINIMUM_SYSTEM_VERSION
perl -pe '
    s/\@DVM_BUNDLE_IDENTIFIER\@/$ENV{DVM_BUNDLE_IDENTIFIER}/g;
    s/\@DVM_BUNDLE_SHORT_VERSION\@/$ENV{DVM_BUNDLE_SHORT_VERSION}/g;
    s/\@DVM_BUNDLE_VERSION\@/$ENV{DVM_BUNDLE_VERSION}/g;
    s/\@DVM_LS_MINIMUM_SYSTEM_VERSION\@/$ENV{DVM_LS_MINIMUM_SYSTEM_VERSION}/g;
' "${plist_src}" > "${out_app}/Contents/Info.plist"

if grep -Eq '@DVM_(BUNDLE_IDENTIFIER|BUNDLE_SHORT_VERSION|BUNDLE_VERSION|LS_MINIMUM_SYSTEM_VERSION)@' "${out_app}/Contents/Info.plist"; then
    die "unsubstituted @DVM_* token(s) remain in ${out_app}/Contents/Info.plist"
fi

# Validate the installed plist with whatever is available.
if command -v plutil >/dev/null 2>&1; then
    plutil -lint "${out_app}/Contents/Info.plist" >/dev/null || die "installed Info.plist failed plutil -lint"
elif command -v xmllint >/dev/null 2>&1; then
    xmllint --noout "${out_app}/Contents/Info.plist" || die "installed Info.plist failed XML validation"
elif command -v python3 >/dev/null 2>&1; then
    python3 -c 'import plistlib,sys; plistlib.load(open(sys.argv[1],"rb"))' "${out_app}/Contents/Info.plist" \
        || die "installed Info.plist failed plistlib parse"
else
    warn "no plist validator (plutil/xmllint/python3) found; skipping Info.plist validation"
fi

# Icon: required when requested, auto-detected otherwise.
if [ -n "${icon_path}" ]; then
    cp -p "${icon_path}" "${resources_dir}/AppIcon.icns"
elif [ -f "${publish_dir}/AppIcon.icns" ]; then
    cp -p "${publish_dir}/AppIcon.icns" "${resources_dir}/AppIcon.icns"
elif [ -f "${publish_dir}/Assets/AppIcon.icns" ]; then
    cp -p "${publish_dir}/Assets/AppIcon.icns" "${resources_dir}/AppIcon.icns"
elif [ -f "${default_icon}" ]; then
    cp -p "${default_icon}" "${resources_dir}/AppIcon.icns"
else
    warn "CFBundleIconFile references 'AppIcon' but no AppIcon.icns was bundled (pass -i to require one)"
fi

# Vocoder dylib: only when the caller provides one; never invented here.
if [ -n "${vocoder_path}" ]; then
    cp -p "${vocoder_path}" "${frameworks_dir}/"
    printf 'Copied vocoder dylib -> %s/%s (note: DllImport resolution may also require an @rpath/install-name setup; see README.md)\n' \
        "${frameworks_dir}" "$(basename "${vocoder_path}")"
else
    printf 'No vocoder dylib supplied: Contents/Frameworks is empty and vocoder features will be unavailable.\n'
fi

if command -v realpath >/dev/null 2>&1; then
    out_app="$(realpath "${out_app}")"
fi
printf 'Built unsigned app bundle: %s\n' "${out_app}"
printf 'NOT signed, NOT notarized. Local launch requires bypassing Gatekeeper (see README.md).\n'
