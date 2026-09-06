# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

from __future__ import annotations

import json
import sys
import tempfile
import unittest
from pathlib import Path


SCRIPTS = Path(__file__).resolve().parents[1]
REPOSITORY = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))

import release_metadata as metadata  # noqa: E402




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

    def test_release_package_names_cover_all_six_targets(self) -> None:
        self.assertEqual(
            (
                "osx-arm64",
                "osx-x64",
                "win-x64",
                "win-arm64",
                "linux-x64",
                "linux-arm64",
            ),
            metadata.TARGETS,
        )
        expected = {
            "osx-arm64": "dvmconsole-1.2.3-osx-arm64.zip",
            "osx-x64": "dvmconsole-1.2.3-osx-x64.zip",
            "win-x64": "dvmconsole-1.2.3-win-x64.zip",
            "win-arm64": "dvmconsole-1.2.3-win-arm64.zip",
            "linux-x64": "DVMConsole-1.2.3-x86_64.AppImage",
            "linux-arm64": "DVMConsole-1.2.3-aarch64.AppImage",
        }
        for target, package in expected.items():
            with self.subTest(target=target):
                self.assertEqual(package, metadata.package_name("1.2.3", target))
        with self.assertRaisesRegex(metadata.MetadataError, "Unsupported release target"):
            metadata.package_name("1.2.3", "freebsd-x64")

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

    def test_spdx_validation_enforces_native_vocoder_license(self) -> None:
        sbom = self.root / "package.spdx.json"
        document = {
            "spdxVersion": "SPDX-2.3",
            "name": "dvmconsole-test",
            "documentNamespace": "https://example.invalid/dvmconsole-test",
            "creationInfo": {"creators": ["Tool: test"]},
            "packages": [
                {
                    "name": "dvmconsole-vocoder-native",
                    "licenseDeclared": "AGPL-3.0-only",
                }
            ],
        }
        sbom.write_text(json.dumps(document), encoding="utf-8")

        metadata.validate_spdx(
            sbom,
            required_package_licenses=["dvmconsole-vocoder-native=AGPL-3.0-only"],
        )
        document["packages"][0]["licenseDeclared"] = "MIT"
        sbom.write_text(json.dumps(document), encoding="utf-8")
        with self.assertRaisesRegex(metadata.MetadataError, "expected AGPL-3.0-only"):
            metadata.validate_spdx(
                sbom,
                required_package_licenses=["dvmconsole-vocoder-native=AGPL-3.0-only"],
            )

    def test_sbom_first_party_license_annotation_is_exact_and_validated(self) -> None:
        sbom = self.root / "package.spdx.json"
        sbom.write_text(
            json.dumps(
                {
                    "spdxVersion": "SPDX-2.3",
                    "name": "dvmconsole-test",
                    "documentNamespace": "https://example.invalid/dvmconsole-test",
                    "creationInfo": {"creators": ["Tool: test"]},
                    "packages": [{"name": "DvmConsole", "licenseDeclared": "NOASSERTION"}],
                }
            ),
            encoding="utf-8",
        )

        metadata.set_spdx_package_license(
            sbom,
            package_names=["DvmConsole"],
            license_id="AGPL-3.0-only",
        )
        metadata.validate_spdx(
            sbom,
            required_package_licenses=["DvmConsole=AGPL-3.0-only"],
        )

    def test_sbom_records_all_first_party_binary_components(self) -> None:
        sbom = self.root / "package.spdx.json"
        sbom.write_text(
            json.dumps(
                {
                    "spdxVersion": "SPDX-2.3",
                    "SPDXID": "SPDXRef-DOCUMENT",
                    "name": "dvmconsole-test",
                    "documentNamespace": "https://example.invalid/dvmconsole-test",
                    "creationInfo": {"creators": ["Tool: test"]},
                    "packages": [{"name": "DvmConsole", "licenseDeclared": "NOASSERTION"}],
                }
            ),
            encoding="utf-8",
        )

        metadata.ensure_first_party_spdx_packages(
            sbom,
            version="0.7.0-rc.1",
            target="linux-arm64",
        )
        document = json.loads(sbom.read_text(encoding="utf-8"))
        packages = {package["name"]: package for package in document["packages"]}
        self.assertEqual(set(packages), {name for name, _ in metadata.FIRST_PARTY_SPDX_PACKAGES})
        for name, purpose in metadata.FIRST_PARTY_SPDX_PACKAGES:
            with self.subTest(package=name):
                self.assertEqual("0.7.0-rc.1", packages[name]["versionInfo"])
                self.assertEqual("AGPL-3.0-only", packages[name]["licenseDeclared"])
                self.assertEqual(purpose, packages[name]["primaryPackagePurpose"])
        metadata.validate_spdx(
            sbom,
            required_package_licenses=[
                f"{name}=AGPL-3.0-only"
                for name, _ in metadata.FIRST_PARTY_SPDX_PACKAGES
            ],
        )

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
        import subprocess
        result = subprocess.run([sys.executable, str(SCRIPTS / "release_metadata.py"), "--help"],
                                check=True, capture_output=True, text=True)
        for command in ("validate-notes", "version-core", "package-name", "validate-sbom",
                        "set-sbom-license", "ensure-first-party-sbom", "create-checksums", "verify-checksums"):
            self.assertIn(command, result.stdout)
        for retired in ("validate-evidence", "build-manifest", "validate-manifest", "hash-file"):
            self.assertNotIn(retired, result.stdout)




if __name__ == "__main__":
    unittest.main()
