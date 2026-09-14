# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
import json
from pathlib import Path
import plistlib
import shutil
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / 'notarize-macos.py'
spec = importlib.util.spec_from_file_location('notarization', SCRIPT)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class MacNotarizationTests(unittest.TestCase):
    @unittest.skipUnless(sys.platform == 'darwin', 'Requires macOS codesign')
    def test_real_codesign_entitlements_survive_archive_readback(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            app = root / 'Probe.app'
            executable = app / 'Contents/MacOS/Probe'
            executable.parent.mkdir(parents=True)
            shutil.copyfile('/usr/bin/true', executable)
            executable.chmod(0o755)
            (app / 'Contents/Info.plist').write_bytes(plistlib.dumps({
                'CFBundleExecutable': 'Probe', 'CFBundleIdentifier': 'local.console.entitlement-probe',
                'CFBundlePackageType': 'APPL',
                'NSMicrophoneUsageDescription': 'Verify the microphone signing policy.'}))
            subprocess.run(['codesign', '--force', '--sign', '-', '--options', 'runtime',
                            '--entitlements', str(module.ROOT / 'packaging/macos/DeveloperID.entitlements'),
                            str(app)], check=True, capture_output=True)
            archive = root / 'probe.zip'
            subprocess.run(['ditto', '-c', '-k', '--keepParent', str(app), str(archive)], check=True)
            subprocess.run(['ditto', '-x', '-k', str(archive), str(root / 'readback')], check=True)
            module.verify_audio_permission(root / 'readback/Probe.app', root)
            self.assertIs(plistlib.loads((root / 'entitlements.plist').read_bytes())[
                'com.apple.security.device.audio-input'], True)

    def test_missing_permission_description_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'Contents').mkdir()
            (root / 'Contents/Info.plist').write_bytes(plistlib.dumps({}))
            result = subprocess.CompletedProcess([], 0, plistlib.dumps({
                'com.apple.security.device.audio-input': True}), b'')
            with patch.object(module.subprocess, 'run', return_value=result):
                with self.assertRaisesRegex(ValueError, 'permission description'):
                    module.verify_audio_permission(root, root)

    def test_accepted_submission_survives_diagnostic_log_failure(self):
        with tempfile.TemporaryDirectory() as folder:
            results = [subprocess.CompletedProcess([], 0, '{"id":"request-id","status":"Accepted"}', ''),
                       subprocess.CompletedProcess([], 1, '', 'Network unavailable')]
            with patch.object(module.subprocess, 'run', side_effect=results):
                module.submit_notarization(Path(folder) / 'app.zip', [], Path(folder))

    def test_signing_policy_permits_audio_input(self):
        policy = plistlib.loads((module.ROOT / 'packaging/macos/DeveloperID.entitlements').read_bytes())
        self.assertIs(policy.get('com.apple.security.device.audio-input'), True)

    def test_signed_app_without_audio_entitlement_is_rejected(self):
        with tempfile.TemporaryDirectory() as folder:
            result = subprocess.CompletedProcess([], 0, plistlib.dumps({}), b'')
            with patch.object(module.subprocess, 'run', return_value=result):
                with self.assertRaisesRegex(ValueError, 'audio-input entitlement'):
                    module.verify_audio_permission(Path(folder), Path(folder))

    def test_empty_notary_response_preserves_error_without_resubmitting(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            result = subprocess.CompletedProcess([], 1, '', 'The request timed out.')
            with patch.object(module.subprocess, 'run', return_value=result) as run:
                with self.assertRaisesRegex(RuntimeError, 'The request timed out'):
                    module.submit_notarization(root / 'app.zip', [], root)
            self.assertEqual(run.call_count, 1)
            self.assertEqual((root / 'submission.stderr.txt').read_text(), result.stderr)

    def test_accepted_response_without_request_id_is_not_delivery_evidence(self):
        with tempfile.TemporaryDirectory() as folder:
            result = subprocess.CompletedProcess([], 0, '{"status":"Accepted"}', '')
            with patch.object(module.subprocess, 'run', return_value=result) as run:
                with self.assertRaisesRegex(RuntimeError, 'request unknown'):
                    module.submit_notarization(Path(folder) / 'app.zip', [], Path(folder))
            self.assertEqual(run.call_count, 1)

    def test_log_failure_does_not_hide_rejection(self):
        with tempfile.TemporaryDirectory() as folder:
            results = [subprocess.CompletedProcess([], 0, '{"id":"request-id","status":"Invalid"}', ''),
                       subprocess.CompletedProcess([], 1, '', 'Network unavailable')]
            with patch.object(module.subprocess, 'run', side_effect=results):
                with self.assertRaisesRegex(RuntimeError, 'Invalid.*request-id'):
                    module.submit_notarization(Path(folder) / 'app.zip', [], Path(folder))

    def test_rejects_traversal_before_extraction(self):
        with tempfile.TemporaryDirectory() as folder:
            archive = Path(folder) / 'bad.zip'
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('DVMConsole.app/../outside', b'bad')
            with self.assertRaises(ValueError):
                module.validate_input(archive)

    def test_rejected_notarization_never_creates_delivery(self):
        self.exercise('Invalid', False)

    def test_accepted_notarization_requires_stapling_and_gatekeeper(self):
        self.exercise('Accepted', True)

    def exercise(self, status, succeeds):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            archive, output = root / 'unsigned.zip', root / 'signed.zip'
            with zipfile.ZipFile(archive, 'w') as package:
                package.writestr('DVMConsole.app/Contents/MacOS/DVM Console', b'host')
            calls = []

            def fake_run(*args, **kwargs):
                args = [str(arg) for arg in args]
                calls.append(args)
                if args[:3] == ['ditto', '-x', '-k']:
                    app = Path(args[-1]) / 'DVMConsole.app'
                    (app / 'Contents/MacOS').mkdir(parents=True)
                    (app / 'Contents/Info.plist').write_bytes(plistlib.dumps({
                        'CFBundleExecutable': 'DVM Console',
                        'NSMicrophoneUsageDescription': 'Transmit microphone audio.'}))
                    (app / 'Contents/MacOS/DVM Console').write_bytes(bytes.fromhex('cffaedfe'))
                if args[:3] == ['ditto', '-c', '-k']:
                    Path(args[-1]).write_bytes(b'packaged')
                return ''

            def fake_subprocess(args, **kwargs):
                if '--entitlements' in args:
                    return subprocess.CompletedProcess(args, 0, plistlib.dumps({
                        'com.apple.security.device.audio-input': True}), b'')
                if args[0] == 'codesign':
                    return subprocess.CompletedProcess(args, 0, '', 'Authority=Developer ID Application: Test\nruntime')
                return subprocess.CompletedProcess(args, 0, json.dumps({'id': 'test-id', 'status': status}), '')

            argv = ['notarize', str(archive), str(output), '--identity', 'test', '--evidence', str(root / 'evidence')]
            with patch.object(sys, 'argv', argv), patch.object(module, 'run', fake_run), \
                    patch.object(module, 'notary_auth', return_value=[]), \
                    patch.object(module.subprocess, 'run', fake_subprocess):
                if succeeds:
                    module.main()
                else:
                    with self.assertRaises(RuntimeError):
                        module.main()
            self.assertEqual(output.exists(), succeeds)
            self.assertEqual(any(call[0] == 'spctl' for call in calls), succeeds)
            self.assertEqual(any(call[:3] == ['xcrun', 'stapler', 'staple'] for call in calls), succeeds)
            self.assertTrue((root / 'evidence/submission.json').exists())
            self.assertTrue(archive.exists())
