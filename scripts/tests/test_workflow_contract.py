# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import plistlib
import re
import sys
import unittest
from pathlib import Path

SCRIPTS = Path(__file__).resolve().parents[1]
REPOSITORY = SCRIPTS.parent
sys.path.insert(0, str(SCRIPTS))
import release_metadata as metadata

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


class WorkflowContractTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.workflow = (REPOSITORY / ".github/workflows/build.yml").read_text(encoding="utf-8")

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

    def test_ci_pins_fnecore_and_enforces_native_license_metadata(self) -> None:
        self.assertIn("2d36ab4bf2eeef170978c2777c468db2ca4121ef", self.workflow)
        self.assertIn("ensure-first-party-sbom", self.workflow)
        for package, _ in metadata.FIRST_PARTY_SPDX_PACKAGES:
            self.assertIn(
                f"--required-package-license {package}=AGPL-3.0-only",
                self.workflow,
            )
        cargo = (REPOSITORY / "native/vocoder/Cargo.toml").read_text(encoding="utf-8")
        self.assertRegex(cargo, r'(?m)^license = "AGPL-3\.0-only"$')

    def test_ci_rebuilds_and_compares_every_package(self) -> None:
        self.assertEqual(3, self.workflow.count("Rebuild reproducibility candidate"))
        self.assertEqual(3, self.workflow.count("Compare independently built package"))
        self.assertIn("SOURCE_DATE_EPOCH", self.workflow)

        linux_builder = (REPOSITORY / "scripts/build-linux-portable.sh").read_text(
            encoding="utf-8"
        )
        linux_package = (
            REPOSITORY / "scripts/package-desktop-linux-appimage.sh"
        ).read_text(encoding="utf-8")
        self.assertIn('--env "SOURCE_DATE_EPOCH=$SOURCE_DATE_EPOCH"', linux_builder)
        for script in (linux_builder, linux_package):
            self.assertIn("REPRODUCIBLE_EPOCH_FALLBACK=946684800", script)
            self.assertIn('[[ ! "$SOURCE_DATE_EPOCH" =~ ^[0-9]+$ ]]', script)
        self.assertIn("export SOURCE_DATE_EPOCH", linux_package)

    def test_ci_audits_every_rid_without_rewriting_locks(self) -> None:
        audit = self.workflow.split("- name: Audit every locked public RID graph", 1)[1]
        audit = audit.split("- name: Audit locked Rust graph", 1)[0]
        for target in metadata.TARGETS:
            self.assertIn(target, audit)
        self.assertIn("--locked-mode", audit)
        self.assertIn("-p:NuGetAudit=true", audit)
        self.assertIn("git diff --exit-code", audit)

    def test_windows_smokes_exact_published_payload_before_packaging(self) -> None:
        publish = self.workflow.index("- name: Publish unsigned Windows desktop output")
        smoke = self.workflow.index("- name: Smoke published Windows payload")
        package = self.workflow.index("- name: Package unsigned Windows release")
        self.assertLess(publish, smoke)
        self.assertLess(smoke, package)
        published_step = self.workflow[smoke:package]
        self.assertIn('Join-Path $publishDirectory "DvmConsole.exe"', published_step)
        self.assertIn('@("--demo", "--smoke-windows", "--smoke-result=$result")', published_step)
        self.assertIn('"--smoke-result=$result"', published_step)
        self.assertIn("invoke-smoke-with-timeout.ps1", published_step)

    def test_windows_packaging_propagates_rid_and_smokes_extracted_archive(self) -> None:
        package = self.workflow.index("- name: Package unsigned Windows release")
        extracted = self.workflow.index("- name: Verify and smoke extracted Windows package")
        self.assertLess(package, extracted)
        section = self.workflow[package:self.workflow.index("- name: Smoke packaged Linux payload")]
        self.assertIn('-Runtime "${{ matrix.rid }}"', section)
        self.assertIn("Expand-Archive", section)
        self.assertIn('verify-publish.ps1 -Runtime "${{ matrix.rid }}"', section)
        self.assertIn("invoke-smoke-with-timeout.ps1", section)


class PackageContractTests(unittest.TestCase):
    def test_publish_uses_complete_rid_specific_locked_dependency_graphs(self) -> None:
        shell_publish = (REPOSITORY / "scripts/publish-desktop.sh").read_text(encoding="utf-8")
        powershell_publish = (REPOSITORY / "scripts/publish-desktop.ps1").read_text(encoding="utf-8")
        directory_props = (REPOSITORY / "src/Directory.Build.props").read_text(encoding="utf-8")
        self.assertIn("--locked-mode", shell_publish)
        self.assertIn("--locked-mode", powershell_publish)
        self.assertNotIn("--force-evaluate", shell_publish)
        self.assertNotIn("--force-evaluate", powershell_publish)
        self.assertIn("packages.$(DvmConsolePackageRuntime).lock.json", directory_props)

        for runtime in metadata.TARGETS:
            with self.subTest(runtime=runtime):
                locks = sorted((REPOSITORY / "src").glob(f"*/packages.{runtime}.lock.json"))
                self.assertGreaterEqual(len(locks), 1)
                self.assertTrue(
                    (REPOSITORY / "src/DvmConsole.Desktop" / f"packages.{runtime}.lock.json").is_file()
                )
                combined = "\n".join(lock.read_text(encoding="utf-8") for lock in locks)
                self.assertNotIn('"Fizzler"', combined)

    def test_linux_builder_pins_package_snapshot_and_sdk_payloads(self) -> None:
        dockerfile = (REPOSITORY / "packaging/linux/Dockerfile").read_text(encoding="utf-8")
        self.assertIn("ARG DEBIAN_SNAPSHOT=20260901T000000Z", dockerfile)
        self.assertIn("snapshot.debian.org/archive/debian/${DEBIAN_SNAPSHOT}", dockerfile)
        self.assertIn("ARG DOTNET_SDK_VERSION=10.0.400", dockerfile)
        self.assertIn("dotnet-sdk-${DOTNET_SDK_VERSION}-linux-${dotnet_arch}.tar.gz", dockerfile)
        self.assertIn("sha512sum --check", dockerfile)
        self.assertNotIn("apt-get install --yes --no-install-recommends dotnet-sdk", dockerfile)

    def test_macos_manifest_requires_explicit_one_time_code_fields(self) -> None:
        with (REPOSITORY / "packaging/macos/Info.plist").open("rb") as source:
            manifest = plistlib.load(source)
        self.assertIs(
            manifest["NSAutoFillRequiresTextContentTypeForOneTimeCodeOnMac"],
            True,
        )

    def test_prerelease_packages_write_numeric_bundle_versions(self) -> None:
        package_script = (REPOSITORY / "scripts/package-desktop.sh").read_text(encoding="utf-8")
        self.assertIn("version-core", package_script)
        self.assertIn("Set :CFBundleShortVersionString $bundle_version", package_script)
        self.assertIn("Set :CFBundleVersion $bundle_version", package_script)
        self.assertNotIn("Set :CFBundleShortVersionString $DVM_RELEASE_VERSION", package_script)
