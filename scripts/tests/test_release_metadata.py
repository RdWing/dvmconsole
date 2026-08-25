from __future__ import annotations

import json
import re
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).resolve().parents[1]
REPOSITORY = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))

import release_metadata as metadata  # noqa: E402


ACTION_PINS = {
    "actions/checkout": ("3d3c42e5aac5ba805825da76410c181273ba90b1", "v7"),
    "actions/setup-dotnet": ("a98b56852c35b8e3190ac28c8c2271da59106c68", "v6"),
    "actions/setup-python": ("ece7cb06caefa5fff74198d8649806c4678c61a1", "v6"),
    "dtolnay/rust-toolchain": ("bc540ba06a4ccee415bb241490e0b25ee8e7d315", "1.85.0"),
    "actions/upload-artifact": ("043fb46d1a93c77aae656e7c1c64a875d1fc6a0a", "v7"),
    "anchore/sbom-action": ("e22c389904149dbc22b58101806040fa8d37a610", "v0.24.0"),
    "actions/download-artifact": ("3e5f45b2cfb9172054b4087a40e8e0b5a5461e7c", "v8"),
    "actions/attest": ("1e69f48acb82d1966a394da916b4c1698aa569d6", "v4"),
}


class ReleaseMetadataTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_directory = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_directory.name)

    def tearDown(self) -> None:
        self.temp_directory.cleanup()

    def test_prerelease_semver_has_numeric_bundle_core(self) -> None:
        self.assertEqual("1.0.0", metadata.semantic_version_core("1.0.0-rc.1"))
        for version in ("01.0.0", "1.0.0-01", "1.0.0-rc..1", "1.0.0-"):
            with self.subTest(version=version):
                with self.assertRaises(metadata.MetadataError):
                    metadata.semantic_version_core(version)

    def test_release_notes_require_the_exact_version(self) -> None:
        notes = self.root / "notes.md"
        notes.write_text("# DVM Console NEO 0.4.0 — Built for busy systems\n", encoding="utf-8")
        self.assertEqual(
            "DVM Console NEO 0.4.0 — Built for busy systems",
            metadata.validate_release_notes(notes, version="0.4.0"),
        )
        with self.assertRaisesRegex(metadata.MetadataError, "0.4.1"):
            metadata.validate_release_notes(notes, version="0.4.1")

    def test_stable_release_notes_reject_draft_markers(self) -> None:
        notes = self.root / "notes.md"
        notes.write_text(
            "# DVM Console NEO 1.0.0 — Built for busy systems\n\n"
            "**Draft: stable publication is not yet approved.**\n",
            encoding="utf-8",
        )
        with self.assertRaisesRegex(metadata.MetadataError, "draft label"):
            metadata.validate_release_notes(notes, version="1.0.0")

    def test_spdx_validation_requires_identity_creator_and_inventory(self) -> None:
        sbom = self.root / "package.spdx.json"
        sbom.write_text(
            json.dumps(
                {
                    "spdxVersion": "SPDX-2.3",
                    "name": "dvmconsole-test",
                    "documentNamespace": "https://example.invalid/dvmconsole-test",
                    "creationInfo": {"creators": ["Tool: test"]},
                    "packages": [{"name": "dvmconsole-test"}],
                }
            ),
            encoding="utf-8",
        )
        metadata.validate_spdx(sbom)
        sbom.write_text("{}", encoding="utf-8")
        with self.assertRaises(metadata.MetadataError):
            metadata.validate_spdx(sbom)

    def test_checksums_are_sorted_and_verify_exact_subjects(self) -> None:
        artifacts = self.root / "artifacts"
        artifacts.mkdir()
        subjects = [artifacts / "b.zip", artifacts / "a.spdx.json"]
        for subject in subjects:
            subject.write_text(subject.name, encoding="utf-8")
        checksums = artifacts / "SHA256SUMS"

        metadata.create_checksums(subjects, checksums)
        metadata.verify_checksums(
            checksums,
            artifacts,
            expected_subjects=[subject.name for subject in subjects],
        )
        names = [line.split("  ", 1)[1] for line in checksums.read_text().splitlines()]
        self.assertEqual(sorted(names), names)

    def test_checksums_reject_tampering_and_subject_drift(self) -> None:
        artifacts = self.root / "artifacts"
        artifacts.mkdir()
        package = artifacts / "package.zip"
        package.write_text("package", encoding="utf-8")
        checksums = artifacts / "SHA256SUMS"
        metadata.create_checksums([package], checksums)

        with self.assertRaisesRegex(metadata.MetadataError, "missing"):
            metadata.verify_checksums(
                checksums,
                artifacts,
                expected_subjects=[package.name, "package.spdx.json"],
            )

        package.write_text("tampered", encoding="utf-8")
        with self.assertRaisesRegex(metadata.MetadataError, "Checksum mismatch"):
            metadata.verify_checksums(checksums, artifacts)

    def test_checksum_path_traversal_is_rejected(self) -> None:
        checksums = self.root / "SHA256SUMS"
        checksums.write_text(f"{'0' * 64}  ../outside\n", encoding="utf-8")
        with self.assertRaisesRegex(metadata.MetadataError, "Invalid SHA256SUMS"):
            metadata.verify_checksums(checksums, self.root)

    def test_cli_exposes_only_workflow_commands(self) -> None:
        source = (SCRIPTS / "release_metadata.py").read_text(encoding="utf-8")
        for command in (
            "validate-notes",
            "version-core",
            "validate-sbom",
            "create-checksums",
            "verify-checksums",
        ):
            self.assertIn(f'subparsers.add_parser("{command}")', source)
        for retired in ("validate-evidence", "build-manifest", "validate-manifest", "hash-file"):
            self.assertNotIn(f'subparsers.add_parser("{retired}")', source)


class WorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = (REPOSITORY / ".github/workflows/build.yml").read_text(encoding="utf-8")

    def test_preserves_three_platform_matrix(self) -> None:
        for target in metadata.TARGETS:
            self.assertIn(f"rid: {target}", self.workflow)

    def test_generates_sboms_and_first_party_attestations(self) -> None:
        self.assertIn(
            "uses: anchore/sbom-action@e22c389904149dbc22b58101806040fa8d37a610 # v0.24.0",
            self.workflow,
        )
        self.assertIn("syft-version: v1.51.0", self.workflow)
        self.assertGreaterEqual(
            self.workflow.count(
                "uses: actions/attest@1e69f48acb82d1966a394da916b4c1698aa569d6 # v4"
            ),
            5,
        )
        self.assertIn("sbom-path:", self.workflow)

    def test_every_action_is_pinned_to_a_reviewed_full_sha(self) -> None:
        uses_lines = [line.strip() for line in self.workflow.splitlines() if line.strip().startswith("uses:")]
        self.assertTrue(uses_lines)
        for line in uses_lines:
            with self.subTest(line=line):
                match = re.fullmatch(r"uses: ([^@\s]+)@([0-9a-f]{40}) # (\S+)", line)
                self.assertIsNotNone(match)
                action, commit, reviewed_ref = match.groups()
                self.assertIn(action, ACTION_PINS)
                self.assertEqual(ACTION_PINS[action], (commit, reviewed_ref))

    def test_attestation_permissions_are_scoped_to_tagged_publisher(self) -> None:
        publisher = self.workflow.split("  publish-release:", 1)[1]
        matrix = self.workflow.split("  publish-release:", 1)[0]
        self.assertIn("if: startsWith(github.ref, 'refs/tags/v')", publisher)
        self.assertIn("id-token: write", publisher)
        self.assertIn("attestations: write", publisher)
        self.assertNotIn("id-token: write", matrix)
        self.assertNotIn("attestations: write", matrix)

    def test_release_suite_is_read_back_and_verified(self) -> None:
        self.assertIn("verify-checksums", self.workflow)
        self.assertIn("gh attestation verify", self.workflow)
        self.assertIn('gh release download "$GITHUB_REF_NAME"', self.workflow)
        self.assertGreaterEqual(self.workflow.count("--expected-subject"), 2)
        self.assertIn("isPrerelease", self.workflow)

    def test_release_title_comes_from_neo_notes_and_is_read_back(self) -> None:
        self.assertIn("release_title=", self.workflow)
        self.assertIn("validate-notes", self.workflow)
        self.assertIn('--title "$DVM_RELEASE_TITLE"', self.workflow)
        self.assertIn("DVM_RELEASE_TITLE: ${{ steps.metadata.outputs.title }}", self.workflow)
        self.assertIn('--json body,isPrerelease,name', self.workflow)

    def test_tagged_publish_requires_neo_ancestry(self) -> None:
        self.assertIn("git merge-base --is-ancestor", self.workflow)
        self.assertIn("refs/remotes/origin/neo", self.workflow)
        self.assertIn("DVM_TAGGED_COMMIT", self.workflow)

    def test_ci_runs_release_metadata_tests(self) -> None:
        self.assertIn("python -m unittest discover -s scripts/tests", self.workflow)

    def test_windows_smokes_exact_published_payload_before_packaging(self) -> None:
        publish = self.workflow.index("- name: Publish unsigned desktop output")
        smoke = self.workflow.index("- name: Smoke published Windows payload")
        package = self.workflow.index("- name: Package unsigned Windows handoff")
        self.assertLess(publish, smoke)
        self.assertLess(smoke, package)
        published_step = self.workflow[smoke:package]
        self.assertIn('Join-Path $publishDirectory "DvmConsole.exe"', published_step)
        self.assertIn("--demo --smoke-windows", published_step)
        self.assertIn('"--smoke-result=$result"', published_step)


class PackageContractTests(unittest.TestCase):
    def test_prerelease_packages_write_numeric_bundle_versions(self) -> None:
        package_script = (REPOSITORY / "scripts/package-desktop.sh").read_text(encoding="utf-8")
        self.assertIn("version-core", package_script)
        self.assertIn("Set :CFBundleShortVersionString $bundle_version", package_script)
        self.assertIn("Set :CFBundleVersion $bundle_version", package_script)
        self.assertNotIn("Set :CFBundleShortVersionString $DVM_RELEASE_VERSION", package_script)


if __name__ == "__main__":
    unittest.main()
