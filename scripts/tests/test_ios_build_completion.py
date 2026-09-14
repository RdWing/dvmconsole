# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""An interrupted dotnet invocation must not certify an old app bundle."""

from pathlib import Path
import shutil
import tempfile
import unittest

from shell_test_support import run_shell, shell_environment, write_executable


class BuildCompletionTests(unittest.TestCase):
    def test_manifest_requires_this_invocations_completion_marker(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            scripts = root / "scripts"
            scripts.mkdir()
            shutil.copyfile(Path(__file__).resolve().parents[1] / "build-ios.sh", scripts / "build-ios.sh")
            # Isolate the shell contract from actual source hashing and compilation.
            (scripts / "write-ios-build-manifest.py").write_text(
                "import pathlib, sys\n"
                "flag = '--capture-source' if '--capture-source' in sys.argv else '--output'\n"
                "path = pathlib.Path(sys.argv[sys.argv.index(flag) + 1])\n"
                "path.parent.mkdir(parents=True, exist_ok=True)\n"
                "path.write_text('{}')\n")
            binaries = root / "bin"
            binaries.mkdir()
            for name, body in {
                "uname": "echo Darwin\n",
                "uuidgen": "echo invocation-nonce\n",
                "dotnet": """for argument in "$@"; do
  case "$argument" in
    /p:ConsoleBuildCompletionPath=*) marker="${argument#*=}" ;;
    /p:ConsoleBuildCompletionNonce=*) nonce="${argument#*=}" ;;
  esac
done
if [ "$FAKE_COMPLETED" = yes ]; then printf '%s\\n' "$nonce" > "$marker"; fi
exit 0
""",
            }.items():
                executable = binaries / name
                write_executable(executable, body)
            candidate = root / "artifacts/ios/iossimulator-arm64/Debug/build-manifest.json"
            candidate.parent.mkdir(parents=True)
            candidate.write_text("old candidate")
            environment = shell_environment(root, binaries)
            for completed in ("no", "yes"):
                result = run_shell(scripts / "build-ios.sh",
                                   env=dict(environment, FAKE_COMPLETED=completed))
                if completed == "no":
                    self.assertNotEqual(0, result.returncode)
                    self.assertIn("did not complete", result.stderr)
                    self.assertFalse(candidate.exists())
                else:
                    self.assertEqual(0, result.returncode, result.stderr)
                    self.assertTrue(candidate.exists())
