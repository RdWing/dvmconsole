# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
import pathlib
import tempfile
import unittest


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "validate_package_target", REPOSITORY / "scripts" / "validate-package-target.py"
)
VALIDATE_TARGET = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(VALIDATE_TARGET)


class ValidatePackageTargetTests(unittest.TestCase):
    def test_cross_drive_output_checks_ancestors_to_the_destination_root(self):
        destination = pathlib.PureWindowsPath("D:/output/app.zip")
        roots = [pathlib.PureWindowsPath("D:/repo"), pathlib.PureWindowsPath("C:/staging")]

        boundary = VALIDATE_TARGET.ancestor_check_boundary(destination, roots)

        self.assertEqual(pathlib.PureWindowsPath("D:/"), boundary)
        self.assertFalse(VALIDATE_TARGET.is_within(destination, roots[1]))

    def test_same_drive_output_retains_the_common_ancestor_boundary(self):
        destination = pathlib.PureWindowsPath("D:/work/output/app.zip")
        roots = [pathlib.PureWindowsPath("D:/work/repo"), pathlib.PureWindowsPath("D:/work/staging")]

        boundary = VALIDATE_TARGET.ancestor_check_boundary(destination, roots)

        self.assertEqual(pathlib.PureWindowsPath("D:/work"), boundary)

    def test_accepts_external_regular_zip_destination(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            repository.mkdir()
            publish.mkdir()
            destination = root / "output" / "app.zip"

            actual = VALIDATE_TARGET.validate_target(
                destination, ".zip", repository, publish, False
            )

            self.assertEqual(destination.resolve(), actual)

    def test_accepts_existing_application_directory_but_not_a_file(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            application = root / "DVMConsole.app"
            repository.mkdir()
            publish.mkdir()
            application.mkdir()

            actual = VALIDATE_TARGET.validate_target(
                application, ".app", repository, publish, False
            )
            self.assertEqual(application.resolve(), actual)

            invalid = root / "NotAnApplication.app"
            invalid.write_text("file", encoding="utf-8")
            with self.assertRaises(ValueError):
                VALIDATE_TARGET.validate_target(
                    invalid, ".app", repository, publish, False
                )

    def test_rejects_wrong_extension_and_protected_trees(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            repository.mkdir()
            publish.mkdir()
            candidates = [
                (root / "output.exe", ".zip"),
                (repository / "release.zip", ".zip"),
                (publish / "release.zip", ".zip"),
            ]
            for candidate, extension in candidates:
                with self.subTest(candidate=candidate):
                    with self.assertRaises(ValueError):
                        VALIDATE_TARGET.validate_target(
                            candidate, extension, repository, publish, False
                        )

    def test_resolves_parent_links_before_protected_tree_check(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            repository.mkdir()
            publish.mkdir()
            linked_parent = root / "linked-publish"
            try:
                linked_parent.symlink_to(publish, target_is_directory=True)
            except OSError:
                self.skipTest("symbolic links are unavailable")

            with self.assertRaises(ValueError):
                VALIDATE_TARGET.validate_target(
                    linked_parent / "release.zip", ".zip", repository, publish, False
                )

    def test_rejects_any_symbolic_link_ancestor(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            external = root / "external"
            repository.mkdir()
            publish.mkdir()
            external.mkdir()
            linked_parent = root / "linked-external"
            try:
                linked_parent.symlink_to(external, target_is_directory=True)
            except OSError:
                self.skipTest("symbolic links are unavailable")

            with self.assertRaisesRegex(ValueError, "ancestor"):
                VALIDATE_TARGET.validate_target(
                    linked_parent / "release.zip", ".zip", repository, publish, False
                )

    def test_rejects_exact_symbolic_link_destination(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            destination = root / "release.zip"
            linked_destination = root / "linked.zip"
            repository.mkdir()
            publish.mkdir()
            destination.write_bytes(b"archive")
            try:
                linked_destination.symlink_to(destination)
            except OSError:
                self.skipTest("symbolic links are unavailable")

            with self.assertRaisesRegex(ValueError, "symbolic link or junction"):
                VALIDATE_TARGET.validate_target(
                    linked_destination, ".zip", repository, publish, False
                )

    def test_rejects_staging_tree_destination(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            publish = root / "publish"
            staging = root / "staging"
            repository.mkdir()
            publish.mkdir()
            staging.mkdir()

            with self.assertRaisesRegex(ValueError, "staging"):
                VALIDATE_TARGET.validate_target(
                    staging / "release.zip",
                    ".zip",
                    repository,
                    publish,
                    False,
                    [staging],
                )

    def test_validates_generated_output_directories(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            repository = root / "repository"
            staging = root / "staging"
            output = root / "output"
            repository.mkdir()
            staging.mkdir()

            actual = VALIDATE_TARGET.validate_target(
                output, None, repository, None, False, [staging]
            )

            self.assertEqual(output.resolve(), actual)
            output.write_text("not a directory", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "directory"):
                VALIDATE_TARGET.validate_target(
                    output, None, repository, None, False, [staging]
                )


if __name__ == "__main__":
    unittest.main()
