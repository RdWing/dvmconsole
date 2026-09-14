# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
import json
import subprocess
import tempfile

spec = importlib.util.spec_from_file_location("ios_smoke", Path(__file__).resolve().parents[1] / "run-ios-simulator-smoke.py")
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)


class SimulatorSelectionTests(unittest.TestCase):
    def inventory(self):
        return {
            "runtimes": [
                {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-26-5", "version": "26.5", "isAvailable": True},
                {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-27-0", "version": "27.0", "isAvailable": False},
            ],
            "devicetypes": [
                {"identifier": "phone", "productFamily": "iPhone", "minRuntimeVersion": 26 << 16},
                {"identifier": "tablet", "productFamily": "iPad"},
                {"identifier": "future-phone", "productFamily": "iPhone", "minRuntimeVersion": 27 << 16},
                {"identifier": "retired-tablet", "productFamily": "iPad", "maxRuntimeVersion": 25 << 16},
            ],
        }

    def test_selects_available_runtime_and_compatible_device_families(self):
        runtime, devices = smoke.select_devices(self.inventory())
        self.assertTrue(runtime.endswith("iOS-26-5"))
        self.assertEqual([("iPhone", "phone"), ("iPad", "tablet")], devices)

    def test_selects_explicit_older_runtime_by_version_or_identifier(self):
        inventory = self.inventory()
        old = {"identifier": "com.apple.CoreSimulator.SimRuntime.iOS-18-4", "version": "18.4", "isAvailable": True}
        inventory["runtimes"].append(old)
        inventory["devicetypes"].append({"identifier": "older-phone", "productFamily": "iPhone"})
        for requested in (old["version"], old["identifier"]):
            runtime, devices = smoke.select_devices(inventory, requested)
            self.assertEqual(old["identifier"], runtime)
            self.assertEqual("older-phone", devices[0][1])

    def test_explicit_unavailable_runtime_does_not_fall_back(self):
        with self.assertRaisesRegex(RuntimeError, "Requested iOS simulator runtime is unavailable"):
            smoke.select_devices(self.inventory(), "27.0")

    def test_reports_missing_runtime(self):
        inventory = self.inventory()
        inventory["runtimes"] = []
        with self.assertRaisesRegex(RuntimeError, "Install an available iOS"):
            smoke.select_devices(inventory)

    def test_requires_both_device_families(self):
        inventory = self.inventory()
        inventory["devicetypes"] = inventory["devicetypes"][:1]
        with self.assertRaisesRegex(RuntimeError, "No compatible iPad"):
            smoke.select_devices(inventory)

    def test_runs_each_protocol_on_both_families_with_distinct_reports(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "artifacts/ios/iossimulator-arm64/Release/bin/DvmConsole.iOS/Release/net10.0-ios/iossimulator-arm64/DvmConsole.iOS.app").mkdir(parents=True)
            (root / "artifacts/ios/iossimulator-arm64/Release/build-manifest.json").write_text("{}")
            with patch.object(smoke, "ROOT", root), patch.object(smoke.subprocess, "check_output", side_effect=[json.dumps(self.inventory()), "phone-id\n", "tablet-id\n"]), patch.object(smoke.subprocess, "run") as run:
                smoke.run("Release", "26.5")
                checks = [call.args[0] for call in run.call_args_list if any("test-ios-native.py" in str(arg) for arg in call.args[0])]
                self.assertEqual(32, len(checks))
                for device in ("phone-id", "tablet-id"):
                    protocol_checks = [command for command in checks if command[2] == device and "--protocol" in command and "--secure" not in command]
                    self.assertEqual(["p25", "nxdn"], [command[-1] for command in protocol_checks])
                evidence_root = root / "artifacts/ios/qualification/ci"
                latest = json.loads((evidence_root / "latest-run.json").read_text())
                reports = evidence_root / latest["reportDirectory"]
                self.assertEqual("passed", latest["status"])
                status = json.loads((reports / "run-status.json").read_text())
                self.assertEqual(33, len(status["completedChecks"]))
                self.assertEqual("Release", status["configuration"])
                self.assertEqual(33, len(list(reports.glob("*.log"))))
                self.assertTrue(all("--compare" in command for command in checks if command[command.index("--check") + 1] == "vocoder"))
                privacy = [call.args[0] for call in run.call_args_list
                           if call.args[0][:3] == ["xcrun", "simctl", "privacy"]]
                self.assertEqual([(device, action) for device in ("phone-id", "tablet-id")
                                  for action in ("grant", "revoke")],
                                 [(command[3], command[4]) for command in privacy])
                for family in ("iphone", "ipad"):
                    self.assertTrue((reports / f"{family}-inputs-granted.log").exists())
                    self.assertTrue((reports / f"{family}-inputs-denied.log").exists())
                    self.assertTrue((reports / f"{family}-transmit.log").exists())
                    self.assertTrue((reports / f"{family}-routes.log").exists())
                    self.assertTrue((reports / f"{family}-help.log").exists())
                    for protocol in ("p25", "nxdn", "p25-secure"):
                        self.assertTrue((reports / f"{family}-network-{protocol}.log").exists())

    def test_failed_check_retires_only_its_owned_simulator(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / "artifacts/ios/iossimulator-arm64/Release/bin/DvmConsole.iOS/Release/net10.0-ios/iossimulator-arm64/DvmConsole.iOS.app").mkdir(parents=True)
            evidence_root = root / "artifacts/ios/qualification/ci"
            evidence_root.mkdir(parents=True)
            stale = evidence_root / "ipad-network.log"
            stale.write_text("PASS\nEarlier candidate\n")
            def execute(command, **kwargs):
                if any("test-ios-native.py" in str(arg) for arg in command):
                    raise subprocess.CalledProcessError(1, command)
            (root / "artifacts/ios/iossimulator-arm64/Release/build-manifest.json").write_text("{}")
            with patch.object(smoke, "ROOT", root), patch.object(smoke.subprocess, "check_output", side_effect=[json.dumps(self.inventory()), "owned-id\n"]), patch.object(smoke.subprocess, "run", side_effect=execute) as run:
                with self.assertRaises(subprocess.CalledProcessError):
                    smoke.run("Release")
                latest = json.loads((evidence_root / "latest-run.json").read_text())
                self.assertEqual("failed", latest["status"])
                reports = evidence_root / latest["reportDirectory"]
                self.assertFalse((reports / "ipad-network.log").exists())
                self.assertEqual("PASS\nEarlier candidate\n", stale.read_text())
                status = json.loads((reports / "run-status.json").read_text())
                self.assertEqual(["desktop-vocoder-reference"], status["completedChecks"])
                self.assertIn("error", status)
                self.assertTrue(status["candidateManifestSha256"])
                cleanup = [call.args[0] for call in run.call_args_list if call.args[0][2] in ("shutdown", "delete")]
                self.assertEqual([["xcrun", "simctl", "shutdown", "owned-id"], ["xcrun", "simctl", "delete", "owned-id"]], cleanup)
