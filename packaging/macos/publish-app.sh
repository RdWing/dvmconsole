#!/usr/bin/env bash
# SPDX-License-Identifier: AGPL-3.0-only
#
# publish-app.sh - Clean, publish, assemble, and verify one macOS Avalonia
# bundle from the current parent/fnecore tree.
#
# This wrapper exists because dotnet's RID-specific output can survive a
# submodule update. It removes only generated Release output for the selected
# RID before publishing; source, configuration, and evidence are untouched.

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"
project="${repo_root}/DvmConsole.Avalonia/DvmConsole.Avalonia.csproj"
rid="${DVM_MACOS_RID:-osx-arm64}"
out_app="${DVM_MACOS_APP_OUT:-${repo_root}/artifacts/macos/${rid}/DvmConsole.app}"
vocoder_path="${VOCODER_DYLIB:-}"
self_contained=true

die() { printf 'publish-app.sh: ERROR: %s\n' "$*" >&2; exit 1; }

usage() {
    cat <<'EOF'
Usage: publish-app.sh [options]

Clean the selected RID's generated Release outputs, publish the Avalonia
application, assemble an unsigned .app, and verify that the bundle contains
exactly the published fnecore and Avalonia assemblies.

Options:
  -r RID       osx-arm64 (default) or osx-x64
  -o PATH      output .app (default: artifacts/macos/<RID>/DvmConsole.app)
  -v PATH      libvocoder.dylib to pass to build-app.sh
  --framework-dependent
               publish without a self-contained runtime
  -h           show this help

The wrapper refuses tracked parent changes, a dirty fnecore checkout, or a
parent gitlink that does not match the checked-out fnecore HEAD. Generated
outputs are ignored under artifacts/ and project bin/obj directories.
EOF
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        -r) [ "$#" -ge 2 ] || die "-r needs an argument"; rid="$2"; shift 2 ;;
        -o) [ "$#" -ge 2 ] || die "-o needs an argument"; out_app="$2"; shift 2 ;;
        -v) [ "$#" -ge 2 ] || die "-v needs an argument"; vocoder_path="$2"; shift 2 ;;
        --framework-dependent) self_contained=false; shift ;;
        -h|--help) usage; exit 0 ;;
        *) die "unknown option: $1 (see -h)" ;;
    esac
done

case "$rid" in
    osx-arm64|osx-x64) ;;
    *) die "unsupported RID: $rid (use osx-arm64 or osx-x64)" ;;
esac
case "$out_app" in
    ""|/|.|..) die "refusing unsafe output path: $out_app" ;;
    *.app) ;;
    *) die "output path must end in .app: $out_app" ;;
esac
[ -f "$project" ] || die "Avalonia project not found: $project"
[ -x "$script_dir/build-app.sh" ] || die "build-app.sh is not executable"
[ -z "$vocoder_path" ] || [ -f "$vocoder_path" ] || die "vocoder dylib not found: $vocoder_path"
command -v dotnet >/dev/null 2>&1 || die "dotnet is required"
command -v git >/dev/null 2>&1 || die "git is required"
command -v cmp >/dev/null 2>&1 || die "cmp is required"

tracked_status="$(git -C "$repo_root" status --porcelain --untracked-files=no)"
[ -z "$tracked_status" ] || die "tracked parent changes present; commit or stash them before a reproducible publish"
submodule_status="$(git -C "$repo_root/fnecore" status --porcelain)"
[ -z "$submodule_status" ] || die "fnecore working tree is dirty"
parent_head="$(git -C "$repo_root" rev-parse HEAD)"
gitlink_sha="$(git -C "$repo_root" ls-tree HEAD fnecore | awk '{print $3}')"
fnecore_head="$(git -C "$repo_root/fnecore" rev-parse HEAD)"
[ "$gitlink_sha" = "$fnecore_head" ] || die "parent fnecore gitlink $gitlink_sha != checkout $fnecore_head"

publish_dir="${repo_root}/DvmConsole.Avalonia/bin/Release/net8.0/${rid}/publish"
for project_dir in fnecore DvmConsole.Core DvmConsole.Platform DvmConsole.Avalonia; do
    rm -rf \
        "${repo_root}/${project_dir}/bin/Release/net8.0/${rid}" \
        "${repo_root}/${project_dir}/obj/Release/net8.0/${rid}"
done

printf 'Parent HEAD       : %s\n' "$parent_head"
printf 'fnecore gitlink   : %s\n' "$fnecore_head"
printf 'RID               : %s\n' "$rid"
printf 'Self-contained    : %s\n' "$self_contained"
printf 'Output            : %s\n' "$out_app"

dotnet restore "$project" -r "$rid"
dotnet publish "$project" \
    -c Release \
    -r "$rid" \
    --self-contained "$self_contained" \
    --no-restore \
    -o "$publish_dir"

build_args=(-p "$publish_dir" -o "$out_app")
[ -z "$vocoder_path" ] || build_args+=(-v "$vocoder_path")
"$script_dir/build-app.sh" "${build_args[@]}"

bundle_dir="${out_app}/Contents/MacOS"
[ -f "${publish_dir}/fnecore.dll" ] || die "publish output has no fnecore.dll"
[ -f "${publish_dir}/DvmConsole.Avalonia.dll" ] || die "publish output has no DvmConsole.Avalonia.dll"
[ -f "${bundle_dir}/fnecore.dll" ] || die "bundle has no fnecore.dll"
[ -f "${bundle_dir}/DvmConsole.Avalonia.dll" ] || die "bundle has no DvmConsole.Avalonia.dll"
cmp "${publish_dir}/fnecore.dll" "${bundle_dir}/fnecore.dll"
cmp "${publish_dir}/DvmConsole.Avalonia.dll" "${bundle_dir}/DvmConsole.Avalonia.dll"

hash_file() {
    if command -v shasum >/dev/null 2>&1; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        sha256sum "$1" | awk '{print $1}'
    fi
}

manifest="${out_app}/Contents/Resources/build-manifest.txt"
mkdir -p "$(dirname "$manifest")"
{
    printf 'parent_head=%s\n' "$parent_head"
    printf 'fnecore_head=%s\n' "$fnecore_head"
    printf 'rid=%s\n' "$rid"
    printf 'self_contained=%s\n' "$self_contained"
    printf 'dotnet=%s\n' "$(dotnet --version)"
    printf 'fnecore_sha256=%s\n' "$(hash_file "${bundle_dir}/fnecore.dll")"
    printf 'avalonia_sha256=%s\n' "$(hash_file "${bundle_dir}/DvmConsole.Avalonia.dll")"
} > "$manifest"

printf 'Verified bundle assemblies match publish output.\n'
printf 'Build manifest      : %s\n' "$manifest"
printf 'Built unsigned app  : %s\n' "$(cd "$(dirname "$out_app")" && pwd)/$(basename "$out_app")"
printf 'NOT signed, NOT notarized.\n'
