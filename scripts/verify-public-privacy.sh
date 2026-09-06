#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
failure=0

verify_public_fixture() {
    local tracked="$1"
    local path="$ROOT_DIR/$tracked"
    local invalid
    invalid="$(/usr/bin/grep -Ei '^[[:space:]]*address:[[:space:]]*' "$path" 2>/dev/null | \
        /usr/bin/grep -Eiv "(^|[[:space:]\"'])(192\.0\.2\.|198\.51\.100\.|203\.0\.113\.|[a-z0-9.-]*\.example(\.local)?[\"']?[[:space:]]*$)" || true)"
    if [[ -n "$invalid" ]]; then
        printf 'Fixture contains a non-documentation address: %s\n' "$tracked" >&2
        failure=1
    fi
    invalid="$(/usr/bin/grep -Ei '^[[:space:]]*(password|presharedKey|kmfPresharedKey):' "$path" 2>/dev/null | \
        /usr/bin/grep -Ev ":[[:space:]]*[\"']?(DEMO_ONLY|RPT_PASSWORD|123ABC1234)[\"']?[[:space:]]*$" || true)"
    if [[ -n "$invalid" ]]; then
        printf 'Fixture contains a non-placeholder credential: %s\n' "$tracked" >&2
        failure=1
    fi
    if /usr/bin/grep -Eiq '^[[:space:]]*encrypted:[[:space:]]*(true|yes|1)' "$path"; then
        printf 'Fixture enables transport encryption: %s\n' "$tracked" >&2
        failure=1
    fi
}

verify_known_key_fixture() {
    local tracked="$1"
    local path="$ROOT_DIR/$tracked"
    while IFS= read -r material; do
        case "${material^^}" in
            000102030405060708090A0B0C0D0E0F101112131415161718191A1B1C1D1E1F|\
            202122232425262728292A2B2C2D2E2F303132333435363738393A3B3C3D3E3F|\
            0011223344|1234)
                ;;
            *)
                printf 'Fixture contains unknown key material: %s\n' "$tracked" >&2
                failure=1
                ;;
        esac
    done < <(/usr/bin/sed -nE "s/^[[:space:]]*key:[[:space:]]*[\"']?([0-9A-Fa-f]+)[\"']?.*/\\1/p" "$path")
}

while IFS= read -r tracked; do
    normalized="/${tracked//\\//}/"
    base="${tracked##*/}"
    case "$base" in
        *.clear)
            case "$tracked" in
                configs/keys.example.clear|configs/keys.demo.clear)
                    ;;
                *)
                    printf 'Tracked operator-private key file: %s\n' "$tracked" >&2
                    failure=1
                    ;;
            esac
            ;;
        UserSettings.json|UserSettings.*.json|settings-snapshot*.json|*.pcap|*.pcapng)
            printf 'Tracked operator-private file: %s\n' "$tracked" >&2
            failure=1
            ;;
    esac
    case "$normalized" in
        */codeplug_generated/*|*/ConfigurationLibrary/*|*/ConfigurationRuntime/*|*/ManagedAssets/*|*/ManagedRecordings/*|*/Recordings/*)
            printf 'Tracked managed-runtime material: %s\n' "$tracked" >&2
            failure=1
            ;;
    esac
done < <(git -C "$ROOT_DIR" ls-files)

# Public examples are explicit, fictional review surfaces. Other tracked
# configuration-shaped files must not contain likely private endpoints,
# populated credentials, or long hexadecimal key material.
while IFS= read -r tracked; do
    case "$tracked" in
        configs/codeplug.example.yml|configs/codeplug.demo.yml)
            verify_public_fixture "$tracked"
            continue
            ;;
        configs/keys.example.clear|configs/keys.demo.clear)
            verify_known_key_fixture "$tracked"
            continue
            ;;
        configs/alias.example.yml|configs/aliases.demo.yml)
            continue
            ;;
        .github/*|native/vocoder/Cargo.lock|src/*/packages*.lock.json)
            continue
            ;;
    esac
    if [[ ! -f "$ROOT_DIR/$tracked" ]]; then
        continue
    fi
    if /usr/bin/grep -Eiq \
        '(^|[^0-9])(10\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|192\.168\.[0-9]{1,3}\.[0-9]{1,3}|172\.(1[6-9]|2[0-9]|3[01])\.[0-9]{1,3}\.[0-9]{1,3})([^0-9]|$)|(^|[[:space:]])(password|presharedKey|kmfPresharedKey):[[:space:]]*[^[:space:]#]+|(^|[^0-9A-Fa-f])[0-9A-Fa-f]{32,}([^0-9A-Fa-f]|$)' \
        "$ROOT_DIR/$tracked"; then
        printf 'Potential private data in tracked configuration surface: %s\n' "$tracked" >&2
        failure=1
    fi
done < <(git -C "$ROOT_DIR" ls-files '*.yml' '*.yaml' '*.json')

# Source, documentation, scripts, manifests, and otherwise unclassified text
# can all contain configuration-shaped raw strings. Discover text from the
# complete tracked inventory instead of maintaining an extension allowlist.
# Crypto vectors remain covered by protocol tests rather than a broad
# hexadecimal heuristic.
while IFS= read -r -d '' tracked; do
    case "$tracked" in
        scripts/verify-public-privacy.sh|native/vocoder/Cargo.lock|src/*/packages*.lock.json)
            continue
            ;;
    esac
    if [[ ! -f "$ROOT_DIR/$tracked" ]]; then
        continue
    fi
    if [[ -s "$ROOT_DIR/$tracked" ]] && ! /usr/bin/grep -Iq . "$ROOT_DIR/$tracked"; then
        continue
    fi
    if /usr/bin/grep -Eiq \
        '(^|[^0-9])(10\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}|192\.168\.[0-9]{1,3}\.[0-9]{1,3}|172\.(1[6-9]|2[0-9]|3[01])\.[0-9]{1,3}\.[0-9]{1,3})([^0-9]|$)' \
        "$ROOT_DIR/$tracked"; then
        printf 'Potential private endpoint in tracked text: %s\n' "$tracked" >&2
        failure=1
    fi
    invalid="$(/usr/bin/grep -Ei '^[[:space:]]*(password|presharedKey|kmfPresharedKey)[[:space:]]*:[[:space:]]*[^[:space:]#]+' \
        "$ROOT_DIR/$tracked" 2>/dev/null | \
        /usr/bin/grep -Eiv ":[[:space:]]*[\"']?(DEMO_ONLY|RPT_PASSWORD|123ABC1234|secret(-[a-z-]+)?|deployment-password|00112233445566778899AABBCCDDEEFF|FFEEDDCCBBAA99887766554433221100)[\"']?[;,]?[[:space:]]*$" || true)"
    invalid+="$(/usr/bin/grep -Ei '^[[:space:]]*(password|presharedKey|kmfPresharedKey)[[:space:]]*=[[:space:]]*[\"'\''][^\"'\'']+[\"'\'']' \
        "$ROOT_DIR/$tracked" 2>/dev/null | \
        /usr/bin/grep -Eiv "=[[:space:]]*[\"'](DEMO_ONLY|RPT_PASSWORD|123ABC1234|password|transport|kmf|not-used|secret(-[a-z-]+)?|deployment-password|001122|334455|00112233445566778899AABBCCDDEEFF|FFEEDDCCBBAA99887766554433221100)[\"'][;,]?[[:space:]]*$" || true)"
    if [[ -n "$invalid" ]]; then
        printf 'Potential private credential in tracked text: %s\n' "$tracked" >&2
        failure=1
    fi
done < <(git -C "$ROOT_DIR" ls-files -z)

if ((failure != 0)); then
    exit 1
fi

printf 'Tracked public privacy surfaces verified.\n'
