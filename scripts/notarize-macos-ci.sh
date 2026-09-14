#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail
set +x
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_ENVIRONMENT:-}" == github-hosted ]] || {
    echo 'CI signing requires a disposable GitHub-hosted runner.' >&2; exit 2;
}
for name in MACOS_DEVELOPER_ID_P12_BASE64 MACOS_DEVELOPER_ID_P12_PASSWORD \
    ASC_API_KEY_P8_BASE64 ASC_API_KEY_ID ASC_API_ISSUER_ID; do
    [[ -n "${!name:-}" ]] || { printf 'Missing secret: %s\n' "$name" >&2; exit 2; }
done
umask 077
signing_dir="$(mktemp -d "$RUNNER_TEMP/neo-macos-signing.XXXXXX")"
keychain="$signing_dir/signing.keychain-db"
source "$(dirname "${BASH_SOURCE[0]}")/ci-signing-keychain.sh"
cleanup() {
    neo_restore_keychain_search_list || true
    security delete-keychain "$keychain" >/dev/null 2>&1 || true
    rm -rf "$signing_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
export SIGNING_DIR="$signing_dir"
python3 - <<'PY'
import base64, os
from pathlib import Path
for variable, filename in [('MACOS_DEVELOPER_ID_P12_BASE64', 'identity.p12'),
                           ('ASC_API_KEY_P8_BASE64', 'api.p8')]:
    (Path(os.environ['SIGNING_DIR']) / filename).write_bytes(
        base64.b64decode(''.join(os.environ[variable].split()), validate=True))
PY
keychain_password="$(openssl rand -hex 24)"
neo_save_keychain_search_list "$signing_dir"
security create-keychain -p "$keychain_password" "$keychain"
neo_add_signing_keychain "$keychain"
security set-keychain-settings -lut 3600 "$keychain"
security unlock-keychain -p "$keychain_password" "$keychain"
security import "$signing_dir/identity.p12" -k "$keychain" \
    -P "$MACOS_DEVELOPER_ID_P12_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$keychain_password" "$keychain" >/dev/null
identity="$(security find-identity -v -p codesigning "$keychain" | \
    sed -nE 's/.* ([A-Fa-f0-9]{40}) "Developer ID Application:.*$/\1/p')"
[[ "$identity" =~ ^[A-Fa-f0-9]{40}$ ]] || {
    echo 'Expected exactly one Developer ID Application identity.' >&2; exit 1;
}
unset MACOS_DEVELOPER_ID_P12_BASE64 MACOS_DEVELOPER_ID_P12_PASSWORD ASC_API_KEY_P8_BASE64
export ASC_API_KEY_FILE="$signing_dir/api.p8"
python3 scripts/notarize-macos.py "$@" --identity "$identity" --keychain "$keychain"
