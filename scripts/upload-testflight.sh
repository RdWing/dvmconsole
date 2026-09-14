#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

# This entry point is deliberately limited to disposable GitHub-hosted runners.
set -euo pipefail
set +x
[[ "${GITHUB_ACTIONS:-}" == true && "${RUNNER_ENVIRONMENT:-}" == github-hosted ]] || {
    echo 'TestFlight delivery requires a GitHub-hosted runner.' >&2; exit 2;
}
for name in IOS_DISTRIBUTION_P12_BASE64 IOS_DISTRIBUTION_P12_PASSWORD \
    IOS_APP_STORE_PROFILE_BASE64 ASC_API_KEY_P8_BASE64 ASC_API_KEY_ID ASC_API_ISSUER_ID; do
    [[ -n "${!name:-}" ]] || { printf 'Missing secret: %s\n' "$name" >&2; exit 2; }
done
[[ "$ASC_API_KEY_ID" =~ ^[A-Za-z0-9]+$ ]] || exit 2
[[ "$GITHUB_RUN_NUMBER" =~ ^[1-9][0-9]*$ && "$GITHUB_RUN_ATTEMPT" =~ ^[1-9][0-9]*$ ]] || exit 2
(( GITHUB_RUN_ATTEMPT < 100 )) || exit 2
export NEO_IOS_BUILD_NUMBER=$((1000 + GITHUB_RUN_NUMBER * 100 + GITHUB_RUN_ATTEMPT))

umask 077
signing_dir="$(mktemp -d "$RUNNER_TEMP/neo-signing.XXXXXX")"
export NEO_IOS_KEYCHAIN="$signing_dir/signing.keychain-db"
profile_path=''
source "$(dirname "${BASH_SOURCE[0]}")/ci-signing-keychain.sh"
cleanup() {
    neo_restore_keychain_search_list || true
    security delete-keychain "$NEO_IOS_KEYCHAIN" >/dev/null 2>&1 || true
    [[ -z "$profile_path" ]] || rm -f "$profile_path"
    rm -rf "$signing_dir"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
export SIGNING_DIR="$signing_dir"
python3 - <<'PY'
import base64, os
from pathlib import Path
root = Path(os.environ['SIGNING_DIR'])
for variable, filename in [('IOS_DISTRIBUTION_P12_BASE64', 'distribution.p12'),
                           ('IOS_APP_STORE_PROFILE_BASE64', 'profile.mobileprovision'),
                           ('ASC_API_KEY_P8_BASE64', 'api.p8')]:
    (root / filename).write_bytes(base64.b64decode(''.join(os.environ[variable].split()), validate=True))
PY
security cms -D -i "$signing_dir/profile.mobileprovision" > "$signing_dir/profile.plist"
export NEO_IOS_PROVISIONING_PROFILE="$(python3 - <<'PY'
import datetime, os, plistlib, uuid
from pathlib import Path
p = plistlib.loads((Path(os.environ['SIGNING_DIR']) / 'profile.plist').read_bytes())
e = p['Entitlements']
assert e['application-identifier'].endswith('.io.jchang.dvmconsole.neo'), 'Wrong app profile'
assert e.get('beta-reports-active') and not e.get('get-task-allow'), 'Not an App Store profile'
assert not p.get('ProvisionedDevices') and not p.get('ProvisionsAllDevices'), 'Not an App Store profile'
assert p['ExpirationDate'] > datetime.datetime.now(datetime.timezone.utc).replace(tzinfo=None), 'Expired profile'
print(str(uuid.UUID(p['UUID'])).upper())
PY
)"
[[ -n "$NEO_IOS_PROVISIONING_PROFILE" ]] || exit 1
profile_dir="$HOME/Library/Developer/Xcode/UserData/Provisioning Profiles"
mkdir -p "$profile_dir"
profile_path="$profile_dir/$NEO_IOS_PROVISIONING_PROFILE.mobileprovision"
# Do not overwrite any pre-existing profile; cleanup owns only our copied file.
[[ ! -e "$profile_path" ]] || { profile_path=''; echo 'Profile already exists' >&2; exit 1; }
cp "$signing_dir/profile.mobileprovision" "$profile_path"
keychain_password="$(openssl rand -hex 24)"
neo_save_keychain_search_list "$signing_dir"
security create-keychain -p "$keychain_password" "$NEO_IOS_KEYCHAIN"
neo_add_signing_keychain "$NEO_IOS_KEYCHAIN"
security set-keychain-settings -lut 3600 "$NEO_IOS_KEYCHAIN"
security unlock-keychain -p "$keychain_password" "$NEO_IOS_KEYCHAIN"
security import "$signing_dir/distribution.p12" -k "$NEO_IOS_KEYCHAIN" \
    -P "$IOS_DISTRIBUTION_P12_PASSWORD" -T /usr/bin/codesign -T /usr/bin/security >/dev/null
security set-key-partition-list -S apple-tool:,apple:,codesign: -s \
    -k "$keychain_password" "$NEO_IOS_KEYCHAIN" >/dev/null
export NEO_IOS_SIGNING_IDENTITY="$(security find-identity -v -p codesigning "$NEO_IOS_KEYCHAIN" | \
    sed -nE 's/.* ([A-Fa-f0-9]{40}) "Apple Distribution:.*$/\1/p')"
[[ "$NEO_IOS_SIGNING_IDENTITY" =~ ^[A-Fa-f0-9]{40}$ ]] || {
    echo 'Expected exactly one valid Apple Distribution identity.' >&2; exit 1;
}
unset IOS_DISTRIBUTION_P12_BASE64 IOS_DISTRIBUTION_P12_PASSWORD IOS_APP_STORE_PROFILE_BASE64 ASC_API_KEY_P8_BASE64
bash scripts/build-ios.sh Release app-store
ipa="$PWD/artifacts/ios/ios-arm64/Release/app-store/package/DVMConsoleNEO.ipa"
xcrun altool --validate-app "$ipa" --api-key "$ASC_API_KEY_ID" \
    --api-issuer "$ASC_API_ISSUER_ID" --p8-file-path "$signing_dir/api.p8"
xcrun altool --upload-package "$ipa" --api-key "$ASC_API_KEY_ID" \
    --api-issuer "$ASC_API_ISSUER_ID" --p8-file-path "$signing_dir/api.p8"
printf 'Uploaded build %s from commit %s. Apple processing and beta review are separate.\n' \
    "$NEO_IOS_BUILD_NUMBER" "$(git rev-parse HEAD)" >> "$GITHUB_STEP_SUMMARY"
