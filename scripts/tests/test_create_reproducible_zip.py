# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

from __future__ import annotations

import hashlib
import importlib.util
import os
import pathlib
import stat
import tempfile
import unittest
from unittest import mock
import zipfile


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "create_reproducible_zip", REPOSITORY / "scripts" / "create-reproducible-zip.py"
)
ARCHIVER = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(ARCHIVER)


class ReproducibleZipTests(unittest.TestCase):
    def test_archive_is_stable_across_source_mtime_changes(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source = root / "DVMConsole.app"
            executable = source / "Contents" / "MacOS" / "DVM Console"
            executable.parent.mkdir(parents=True)
            executable.write_bytes(b"apphost")
            executable.chmod(0o755)
            resource = source / "Contents" / "Resources" / "readme.txt"
            resource.parent.mkdir(parents=True)
            resource.write_text("documentation", encoding="utf-8")
            first = root / "first.zip"
            second = root / "second.zip"

            ARCHIVER.create_archive(source, first, 1_700_000_001)
            os.utime(executable, (1_800_000_000, 1_800_000_000))
            os.utime(resource, (1_600_000_000, 1_600_000_000))
            ARCHIVER.create_archive(source, second, 1_700_000_001)

            self.assertEqual(
                hashlib.sha256(first.read_bytes()).digest(),
                hashlib.sha256(second.read_bytes()).digest(),
            )
            with zipfile.ZipFile(first) as package:
                entries = package.infolist()
                self.assertEqual(
                    sorted(entry.filename for entry in entries),
                    [entry.filename for entry in entries],
                )
                apphost = package.getinfo("DVMConsole.app/Contents/MacOS/DVM Console")
                self.assertEqual((2023, 11, 14, 22, 13, 20), apphost.date_time)

    def test_archive_preserves_posix_executable_permissions(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary).resolve()
            source = root / "package"
            source.mkdir()
            executable = source / "apphost"
            executable.write_bytes(b"apphost")
            archive = root / "package.zip"
            real_stat = pathlib.Path.stat

            def source_stat(path, *args, **kwargs):
                result = real_stat(path, *args, **kwargs)
                if path == executable:
                    # Windows chmod cannot set POSIX execute bits. Supply the
                    # same source metadata on every host for this contract test.
                    fields = list(result)
                    fields[0] = stat.S_IFREG | 0o755
                    return os.stat_result(fields)
                return result

            with mock.patch.object(pathlib.Path, "stat", source_stat):
                ARCHIVER.create_archive(source, archive, 1_700_000_000)

            with zipfile.ZipFile(archive) as package:
                apphost = package.getinfo("package/apphost")
                self.assertEqual(stat.S_IFREG | 0o755, apphost.external_attr >> 16)

    def test_rejects_symbolic_links(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            source = root / "package"
            source.mkdir()
            target = root / "target"
            target.write_text("target", encoding="utf-8")
            link = source / "link"
            try:
                link.symlink_to(target)
            except OSError:
                self.skipTest("symbolic links are unavailable")

            with self.assertRaisesRegex(ValueError, "symbolic link"):
                ARCHIVER.create_archive(source, root / "package.zip", 1_700_000_000)


if __name__ == "__main__":
    unittest.main()
