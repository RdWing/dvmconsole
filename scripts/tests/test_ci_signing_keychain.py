# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import json
from pathlib import Path
import shutil
import tempfile
import unittest

from shell_test_support import run_shell, shell_environment, write_executable

HELPER = Path(__file__).resolve().parents[1] / 'ci-signing-keychain.sh'


class SigningKeychainTests(unittest.TestCase):
    def test_restores_existing_search_list_after_success_and_failure(self):
        for existing in ([], ['/runner/Login Keychain.keychain-db', '/Library/Keychains/System.keychain']):
            for exit_code in (0, 7):
                with self.subTest(existing=existing, exit_code=exit_code), tempfile.TemporaryDirectory() as folder:
                    root = Path(folder)
                    binaries = root / 'bin'
                    binaries.mkdir()
                    (root / 'state').mkdir()
                    shutil.copyfile(HELPER, root / 'helper.sh')
                    (root / 'initial-list').write_text('\n'.join(json.dumps(path) for path in existing))
                    write_executable(binaries / 'security', '''if [[ "${4:-}" == -s ]]; then
    printf '<call>\\n' >> "$TEST_ROOT/calls"
    printf '%s\\n' "$@" >> "$TEST_ROOT/calls"
else
    cat "$TEST_ROOT/initial-list"
fi
''')
                    write_executable(root / 'exercise.sh', '''set -euo pipefail
source ./helper.sh
trap neo_restore_keychain_search_list EXIT
neo_save_keychain_search_list ./state
neo_add_signing_keychain '/runner/Temporary Signing.keychain-db'
exit "$SIMULATED_EXIT"
''')
                    env = shell_environment(root, binaries, TEST_ROOT=root, SIMULATED_EXIT=str(exit_code))
                    result = run_shell('exercise.sh', env=env, cwd=root)
                    self.assertEqual(result.returncode, exit_code, result.stderr)
                    calls = [call.splitlines() for call in (root / 'calls').read_text().split('<call>\n')[1:]]
                    prefix = ['list-keychains', '-d', 'user', '-s']
                    self.assertEqual(calls, [prefix + ['/runner/Temporary Signing.keychain-db'] + existing,
                                             prefix + existing])
