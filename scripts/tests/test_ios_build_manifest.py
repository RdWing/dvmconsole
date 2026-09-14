# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
import json
from pathlib import Path
import subprocess
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("ios_manifest", Path(__file__).resolve().parents[1] / "write-ios-build-manifest.py")
manifest = importlib.util.module_from_spec(spec)
spec.loader.exec_module(manifest)


class SourceIdentityTests(unittest.TestCase):
    def test_captures_dirty_untracked_deleted_and_submodule_inputs_but_not_ignored_files(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            def git(*args, cwd=root):
                return subprocess.run(["git", *args], cwd=cwd, check=True, capture_output=True)
            git("init", "-q")
            git("config", "user.email", "test@example.invalid")
            git("config", "user.name", "Test")
            (root / ".gitignore").write_text("ignored\n")
            (root / "source.cs").write_text("original")
            git("add", ".")
            git("commit", "-qm", "fixture")
            initial = manifest.capture_source(root)
            self.assertFalse(initial["hasLocalChanges"])
            (root / "ignored").write_text("private")
            self.assertEqual(initial, manifest.capture_source(root))
            (root / "source.cs").write_text("edited")
            edited = manifest.capture_source(root)
            self.assertNotEqual(initial["treeSha256"], edited["treeSha256"])
            (root / "new.cs").write_text("new")
            added = manifest.capture_source(root)
            self.assertNotEqual(edited["treeSha256"], added["treeSha256"])
            self.assertNotIn("new.cs", json.dumps(added))
            (root / "source.cs").unlink()
            self.assertNotEqual(added["treeSha256"], manifest.capture_source(root)["treeSha256"])
            nested = root / "native"
            nested.mkdir()
            git("init", "-q", cwd=nested)
            (nested / "codec.c").write_text("codec")
            git("add", ".", cwd=nested)
            git("-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-qm", "codec", cwd=nested)
            git("add", "native")
            before = manifest.capture_source(root)
            (nested / "codec.c").write_text("changed codec")
            after = manifest.capture_source(root)
            self.assertNotEqual(before["treeSha256"], after["treeSha256"])
            self.assertEqual(before["submodules"], after["submodules"])
            snapshot = root / "ignored"
            snapshot.write_text(json.dumps(after))
            self.assertEqual(after, manifest.checked_source(root, snapshot))
            (nested / "codec.c").write_text("changed during build")
            with self.assertRaisesRegex(RuntimeError, "Source changed during"):
                manifest.checked_source(root, snapshot)

    def test_qualification_rejects_stale_source_and_modified_bundle(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            app = root / "app"
            app.mkdir()
            binary = app / "binary"
            binary.write_bytes(b"candidate")
            source = {"treeSha256": "source"}
            candidate = root / "manifest.json"
            candidate.write_text(json.dumps({
                "sourceVerifiedUnchangedDuringBuild": True,
                "sourceSnapshot": source,
                "bundleManifestSha256": manifest.capture_bundle(app)[1],
            }))
            with patch.object(manifest, "capture_source", return_value=source):
                manifest.verify_build(root, app, candidate)
                binary.write_bytes(b"modified")
                with self.assertRaisesRegex(RuntimeError, "bundle differs"):
                    manifest.verify_build(root, app, candidate)
            with patch.object(manifest, "capture_source", return_value={"treeSha256": "later source"}):
                with self.assertRaisesRegex(RuntimeError, "does not match current source"):
                    manifest.verify_build(root, app, candidate)


class EncryptionDeclarationTests(unittest.TestCase):
    def test_preserves_explicit_boolean_declaration(self):
        for value in (False, True):
            self.assertIs(manifest.encryption_declaration(
                {'ITSAppUsesNonExemptEncryption': value}), value)

    def test_missing_or_mistyped_declaration_is_not_silently_exempt(self):
        for value in (None, 'false', 0):
            with self.assertRaisesRegex(RuntimeError, 'Boolean'):
                manifest.encryption_declaration({'ITSAppUsesNonExemptEncryption': value})
        with self.assertRaises(RuntimeError):
            manifest.encryption_declaration({})
