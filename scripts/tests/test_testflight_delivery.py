# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Exercise the delivery script without credentials, Apple services or a build."""
import base64
import os
from pathlib import Path
import plistlib
import shutil
import tempfile
import unittest
from datetime import datetime, timedelta

from shell_test_support import run_shell, shell_environment, write_executable

SCRIPT = Path(__file__).resolve().parents[1] / 'upload-testflight.sh'


class TestFlightDeliveryTests(unittest.TestCase):
    def test_refuses_local_execution(self):
        env = dict(os.environ)
        env.pop('GITHUB_ACTIONS', None)
        result = run_shell(SCRIPT, env=env)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('GitHub-hosted', result.stderr)

    def test_validation_failure_prevents_upload_and_cleans_signing_files(self):
        self.run_delivery(validation_exit=1, expect_upload=False)

    def test_success_uploads_and_cleans_signing_files(self):
        self.run_delivery(validation_exit=0, expect_upload=True)

    def run_delivery(self, validation_exit, expect_upload):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'scripts').mkdir()
            shutil.copyfile(SCRIPT, root / 'scripts/upload-testflight.sh')
            shutil.copyfile(SCRIPT.with_name('ci-signing-keychain.sh'), root / 'scripts/ci-signing-keychain.sh')
            write_executable(root / 'scripts/build-ios.sh',
                'mkdir -p artifacts/ios/ios-arm64/Release/app-store/package\n'
                'touch artifacts/ios/ios-arm64/Release/app-store/package/DVMConsoleNEO.ipa\n')
            binaries = root / 'bin'
            binaries.mkdir()
            profile = {'UUID': '11111111-2222-3333-4444-555555555555',
                       'ExpirationDate': datetime.now() + timedelta(days=10),
                       'Entitlements': {'application-identifier': 'TEST.io.jchang.dvmconsole.neo',
                                        'beta-reports-active': True, 'get-task-allow': False}}
            (root / 'profile.plist').write_bytes(plistlib.dumps(profile))
            for name, body in {
                'security': '''case "$1" in
cms) cat "$TEST_ROOT/profile.plist" ;;
find-identity) echo '  1) AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA "Apple Distribution: Test"' ;;
delete-keychain) touch "$TEST_ROOT/cleaned" ;;
esac
''',
                'xcrun': '''echo "$2" >> "$TEST_ROOT/apple-commands"
if [[ "$2" == --validate-app ]]; then exit "$VALIDATION_EXIT"; fi
''',
                'git': 'echo tested-commit\n',
            }.items():
                path = binaries / name
                write_executable(path, body)
            env = shell_environment(root, binaries,
                       HOME=root / 'home', RUNNER_TEMP=root, TEST_ROOT=root,
                       GITHUB_ACTIONS='true', RUNNER_ENVIRONMENT='github-hosted',
                       GITHUB_RUN_NUMBER='1', GITHUB_RUN_ATTEMPT='1',
                       GITHUB_STEP_SUMMARY=root / 'summary', VALIDATION_EXIT=str(validation_exit),
                       IOS_DISTRIBUTION_P12_PASSWORD='fake-password', ASC_API_KEY_ID='TEST',
                       ASC_API_ISSUER_ID='test-issuer')
            for key in ('IOS_DISTRIBUTION_P12_BASE64', 'IOS_APP_STORE_PROFILE_BASE64',
                        'ASC_API_KEY_P8_BASE64'):
                env[key] = base64.b64encode(b'fake fixture').decode()
            result = run_shell('scripts/upload-testflight.sh', cwd=root, env=env)
            self.assertEqual(result.returncode == 0, expect_upload, result.stderr)
            self.assertTrue((root / 'apple-commands').exists(), result.stderr)
            commands = (root / 'apple-commands').read_text()
            self.assertEqual('--upload-package' in commands, expect_upload)
            self.assertTrue((root / 'cleaned').exists())
            self.assertEqual(list(root.glob('neo-signing.*')), [])
            self.assertEqual(list((root / 'home').rglob('*.mobileprovision')), [])
            if expect_upload:
                self.assertIn('1101 from commit tested-commit', (root / 'summary').read_text())
