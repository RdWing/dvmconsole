#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
configuration="${1:-Debug}"
mode="${2:-simulator}"
if [[ $# -gt 2 || ( "$configuration" != Debug && "$configuration" != Release ) ]]; then
    printf 'Usage: %s [Debug|Release] [simulator|unsigned-device|signed-private-device|app-store]\n' "$0" >&2
    exit 2
fi
runtime=iossimulator-arm64
build_arguments=(/p:RestoreLockedMode=true)
case "$mode" in
    simulator) ;;
    unsigned-device)
        runtime=ios-arm64
        build_arguments+=(/p:EnableCodeSigning=false)
        ;;
    signed-private-device|app-store)
        runtime=ios-arm64
        if [[ -z "${NEO_IOS_SIGNING_IDENTITY:-}" || -z "${NEO_IOS_PROVISIONING_PROFILE:-}" ]]; then
            printf 'Signed builds require NEO_IOS_SIGNING_IDENTITY and NEO_IOS_PROVISIONING_PROFILE.\n' >&2
            exit 2
        fi
        if [[ "$mode" == app-store ]]; then
            if [[ "$configuration" != Release || -z "${NEO_IOS_BUILD_NUMBER:-}" ]]; then
                printf 'App Store builds require Release and NEO_IOS_BUILD_NUMBER.\n' >&2
                exit 2
            fi
            build_arguments+=(/p:BuildIpa=true /p:ArchiveOnBuild=true)
        fi
        build_arguments+=(/p:EnableCodeSigning=true
            "/p:CodesignKey=$NEO_IOS_SIGNING_IDENTITY"
            "/p:CodesignProvision=$NEO_IOS_PROVISIONING_PROFILE")
        ;;
    *) printf 'Unknown iOS build mode: %s\n' "$mode" >&2; exit 2 ;;
esac
if [[ -n "${NEO_IOS_KEYCHAIN:-}" ]]; then
    build_arguments+=("/p:CodesignKeychain=$NEO_IOS_KEYCHAIN")
fi
if [[ -n "${NEO_IOS_BUILD_NUMBER:-}" ]]; then
    if [[ ! "$NEO_IOS_BUILD_NUMBER" =~ ^[1-9][0-9]*$ ]]; then
        printf 'NEO_IOS_BUILD_NUMBER must be a positive integer.\n' >&2
        exit 2
    fi
    build_arguments+=("/p:ApplicationVersion=$NEO_IOS_BUILD_NUMBER")
fi
if [[ "$(uname -s)" != Darwin ]]; then
    printf 'The iOS build requires macOS, Xcode, and the .NET iOS workload.\n' >&2
    exit 1
fi

build_root="$ROOT_DIR/artifacts/ios/$runtime/$configuration"
# Keep private signing products separate from unsigned compilation evidence.
if [[ "$mode" == signed-private-device ]]; then build_root="$build_root/private-signed"; fi
if [[ "$mode" == app-store ]]; then
    build_root="$build_root/app-store"
    build_arguments+=("/p:IpaPackageDir=$build_root/package" "/p:IpaPackageName=DVMConsoleNEO.ipa")
fi
cd "$ROOT_DIR"
build_evidence="$(mktemp -d "${TMPDIR:-/tmp}/neo-ios-build.XXXXXX")"
trap 'rm -rf "$build_evidence"' EXIT
source_snapshot="$build_evidence/source.json"
completion_nonce="$(uuidgen)"
rm -f "$build_root/build-manifest.json"
python3 scripts/write-ios-build-manifest.py --capture-source "$source_snapshot"
dotnet build src/DvmConsole.iOS/DvmConsole.iOS.csproj \
    /t:ConsoleBuildWithCompletionEvidence \
    /p:ConsoleBuildCompletionPath="$build_evidence/completed" \
    /p:ConsoleBuildCompletionNonce="$completion_nonce" \
    --configuration "$configuration" \
    --disable-build-servers \
    /m:1 /p:UseSharedCompilation=false \
    /p:DvmConsoleIosRuntime="$runtime" \
    /p:DvmConsoleIsolatedBuildRoot="$build_root" \
    "${build_arguments[@]}"

# An interrupted dotnet process can return zero before native linking finishes.
# Only the final MSBuild target can attest that this invocation completed.
if [[ ! -f "$build_evidence/completed" || "$(cat "$build_evidence/completed")" != "$completion_nonce" ]]; then
    printf 'The iOS build did not complete; no candidate manifest was written.\n' >&2
    exit 1
fi

app_path="$build_root/bin/DvmConsole.iOS/$configuration/net10.0-ios/$runtime/DvmConsole.iOS.app"
python3 scripts/write-ios-build-manifest.py "$app_path" --configuration "$configuration" \
    --runtime "$runtime" --mode "$mode" --source-snapshot "$source_snapshot" \
    --output "$build_root/build-manifest.json"
printf '\niOS app (%s): %s\n' "$mode" "$app_path"

if [[ "$mode" == app-store ]]; then
    ipa_path="$build_root/package/DVMConsoleNEO.ipa"
    if [[ ! -s "$ipa_path" ]]; then
        printf 'App Store build did not produce its IPA.\n' >&2
        exit 1
    fi
    shasum -a 256 "$ipa_path" > "$ipa_path.sha256"
    printf '\nApp Store package (not uploaded): %s\n' "$ipa_path"
fi
