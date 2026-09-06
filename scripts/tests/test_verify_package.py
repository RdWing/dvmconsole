# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
import pathlib
import tempfile
import unittest
import zipfile


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "verify_package", REPOSITORY / "scripts" / "verify-package.py"
)
VERIFY_PACKAGE = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(VERIFY_PACKAGE)


class VerifyPackageTests(unittest.TestCase):
    def test_windows_inventory_must_match_publish_exactly(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish = root / "publish"
            publish.mkdir()
            (publish / "DvmConsole.exe").write_bytes(b"exe")
            archive = root / "package.zip"
            self._write_archive(
                archive,
                {"DVMConsole-win-x64/DvmConsole.exe": b"exe"},
            )

            VERIFY_PACKAGE.verify_archive(archive, publish, "win-x64")

            self._write_archive(
                archive,
                {
                    "DVMConsole-win-x64/DvmConsole.exe": b"exe",
                    "DVMConsole-win-x64/stale.pdb": b"stale",
                },
            )
            with self.assertRaisesRegex(ValueError, "unexpected"):
                VERIFY_PACKAGE.verify_archive(archive, publish, "win-x64")

    def test_macos_inventory_accounts_for_bundle_files_and_apphost_name(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish = root / "publish"
            publish.mkdir()
            (publish / "DvmConsole").write_bytes(b"host")
            (publish / "DvmConsole.dll").write_bytes(b"managed")
            archive = root / "package.zip"
            self._write_archive(
                archive,
                {
                    "DVMConsole.app/Contents/Info.plist": b"plist",
                    "DVMConsole.app/Contents/Resources/DVMConsole.icns": b"icon",
                    "DVMConsole.app/Contents/MacOS/DVM Console": b"host",
                    "DVMConsole.app/Contents/MacOS/DvmConsole.dll": b"managed",
                },
            )

            VERIFY_PACKAGE.verify_archive(archive, publish, "osx-arm64")

    def test_rejects_traversal_and_symbolic_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish = root / "publish"
            publish.mkdir()
            (publish / "DvmConsole.exe").write_bytes(b"exe")
            archive = root / "package.zip"
            with zipfile.ZipFile(archive, "w") as package:
                package.writestr("../outside", b"bad")
            with self.assertRaisesRegex(ValueError, "unsafe"):
                VERIFY_PACKAGE.verify_archive(archive, publish, "win-x64")

    def test_archive_bytes_and_modes_must_match_staging(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            publish = root / "publish"
            publish.mkdir()
            (publish / "DvmConsole.exe").write_bytes(b"exe")
            staged = root / "DVMConsole-win-x64"
            staged.mkdir()
            executable = staged / "DvmConsole.exe"
            executable.write_bytes(b"exe")
            executable.chmod(0o755)
            archive = root / "package.zip"
            self._write_archive(
                archive,
                {"DVMConsole-win-x64/DvmConsole.exe": b"changed"},
            )

            with self.assertRaisesRegex(ValueError, "content"):
                VERIFY_PACKAGE.verify_archive(
                    archive, publish, "win-x64", staged
                )

            with zipfile.ZipFile(archive, "w") as package:
                info = zipfile.ZipInfo("DVMConsole-win-x64/DvmConsole.exe")
                info.create_system = 3
                info.external_attr = (0o100644 << 16)
                package.writestr(info, b"exe")
            with self.assertRaisesRegex(ValueError, "mode"):
                VERIFY_PACKAGE.verify_archive(
                    archive, publish, "win-x64", staged
                )

    @staticmethod
    def _write_archive(archive: pathlib.Path, entries: dict[str, bytes]) -> None:
        with zipfile.ZipFile(archive, "w") as package:
            for name, content in entries.items():
                package.writestr(name, content)




class PublishInventoryTests(unittest.TestCase):
    def setUp(self):
        import shutil
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = pathlib.Path(self.temporary.name)
        (self.root / "LICENSE").write_text("test")
        (self.root / "DvmConsole.exe").write_bytes(b"test")
        shutil.copytree(REPOSITORY / "docs/user-guide", self.root / "Documentation")

    def test_valid_publish_and_missing_legal_notice(self):
        VERIFY_PACKAGE.verify_publish(self.root, "win-x64")
        (self.root / "LICENSE").unlink()
        with self.assertRaisesRegex(ValueError, "LICENSE"):
            VERIFY_PACKAGE.verify_publish(self.root, "win-x64")

    def test_required_page_cannot_disappear_from_both_manifest_and_payload(self):
        import json
        manifest = self.root / "Documentation/manifest.json"
        value = json.loads(manifest.read_text())
        page = "Getting Started/01-Overview.md"
        value["pages"].remove(page)
        manifest.write_text(json.dumps(value))
        (self.root / "Documentation" / page).unlink()
        with self.assertRaisesRegex(ValueError, "missing documentation"):
            VERIFY_PACKAGE.verify_publish(self.root, "win-x64")

    def test_rejects_debug_directories_and_files(self):
        for name in ["native.dSYM", "native.pdb"]:
            with self.subTest(name=name):
                path = self.root / name
                path.mkdir()
                with self.assertRaisesRegex(ValueError, "debugging"):
                    VERIFY_PACKAGE.verify_publish(self.root, "win-x64")
                path.rmdir()

    def test_rejects_sparse_oversize_and_excess_files(self):
        path = self.root / "large"
        with path.open("wb") as output:
            output.truncate(181 * 1024 * 1024)
        with self.assertRaisesRegex(ValueError, "byte size"):
            VERIFY_PACKAGE.verify_publish(self.root, "win-x64")
        path.unlink()
        for index in range(251):
            (self.root / f"extra-{index}").touch()
        with self.assertRaisesRegex(ValueError, "file budget"):
            VERIFY_PACKAGE.verify_publish(self.root, "win-x64")

    def test_rejects_unknown_rid_and_obsolete_docs(self):
        with self.assertRaisesRegex(ValueError, "unsupported"):
            VERIFY_PACKAGE.verify_publish(self.root, "unknown")
        (self.root / "Docs").mkdir()
        with self.assertRaisesRegex(ValueError, "obsolete"):
            VERIFY_PACKAGE.verify_publish(self.root, "win-x64")

if __name__ == "__main__":
    unittest.main()
