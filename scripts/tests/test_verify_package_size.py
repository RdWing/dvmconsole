# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import os
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[1] / "verify-package-size.py"


class PackageSizeTests(unittest.TestCase):
    def test_cli_limits_and_inputs(self):
        with tempfile.TemporaryDirectory() as directory:
            package = pathlib.Path(directory) / "package.zip"
            cases = [(0, "unsupported", "", False)]
            for rid, mib in [("osx-arm64", 30), ("osx-x64", 30), ("win-x64", 25),
                             ("win-arm64", 25), ("linux-x64", 25), ("linux-arm64", 25)]:
                cases.extend([(mib * 1024 * 1024, rid, "", True),
                              (mib * 1024 * 1024 + 1, rid, "", False),
                              (mib * 1024 * 1024 + 1, rid, rid, False)])
            for size, rid, exception, passes in cases:
                with self.subTest(size=size, rid=rid, exception=exception):
                    with package.open("wb") as output:
                        output.truncate(size)
                    result = subprocess.run(
                        [sys.executable, str(SCRIPT), "--rid", rid, "--package", str(package)],
                        env={**os.environ, "DVM_PACKAGE_SIZE_EXCEPTION": exception},
                        capture_output=True, text=True,
                    )
                    self.assertEqual(passes, result.returncode == 0, result.stderr)
            package.unlink()
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--rid", "win-x64", "--package", str(package)],
                capture_output=True, text=True,
            )
            self.assertNotEqual(0, result.returncode)
            self.assertIn("does not exist", result.stderr)
