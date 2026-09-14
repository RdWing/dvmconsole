#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Run isolated native smoke checks on temporary iPhone and iPad simulators."""

import argparse
from datetime import datetime, timezone
import hashlib
import json
import os
import shutil
from pathlib import Path
import subprocess
import sys
import uuid

ROOT = Path(__file__).resolve().parents[1]


def select_devices(inventory, requested_runtime=None):
    runtimes = [item for item in inventory["runtimes"]
                if item.get("isAvailable") and ".iOS-" in item["identifier"]]
    if not runtimes:
        raise RuntimeError("Install an available iOS simulator runtime before running smoke checks")
    if requested_runtime is not None:
        runtimes = [item for item in runtimes if requested_runtime in (item["version"], item["identifier"])]
        if not runtimes:
            raise RuntimeError(f"Requested iOS simulator runtime is unavailable: {requested_runtime}")
    runtime = max(runtimes, key=lambda item: tuple(map(int, item["version"].split("."))))
    version = tuple(map(int, runtime["version"].split(".")))
    encoded = sum(value << shift for value, shift in zip((*version, 0, 0)[:3], (16, 8, 0)))
    devices = []
    for family in ("iPhone", "iPad"):
        compatible = [item for item in inventory["devicetypes"]
                      if item.get("productFamily") == family
                      and item.get("minRuntimeVersion", 0) <= encoded <= item.get("maxRuntimeVersion", 0xFFFFFFFF)]
        if not compatible:
            raise RuntimeError(f"No compatible {family} simulator type for iOS {runtime['version']}")
        devices.append((family, compatible[0]["identifier"]))
    return runtime["identifier"], devices


def write_json_atomic(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


class RunEvidence:
    """Keep incomplete runs distinct from every earlier candidate's results."""

    def __init__(self, configuration, runtime, candidate):
        self.root = ROOT / "artifacts/ios/qualification/ci"
        started = datetime.now(timezone.utc)
        self.run_id = started.strftime("%Y%m%dT%H%M%SZ") + "-" + uuid.uuid4().hex[:12]
        self.reports = self.root / "runs" / self.run_id
        self.reports.mkdir(parents=True)
        self.status = {
            "runId": self.run_id, "status": "running", "configuration": configuration,
            "runtime": runtime, "startedAt": started.isoformat(), "completedChecks": [],
            "candidateManifestSha256": hashlib.sha256(candidate.read_bytes()).hexdigest(),
        }
        shutil.copyfile(candidate, self.reports / "build-manifest.json")
        self.publish()
        print(f"Native smoke reports: {self.reports}", flush=True)

    def publish(self):
        write_json_atomic(self.reports / "run-status.json", self.status)
        write_json_atomic(self.root / "latest-run.json", {
            "runId": self.run_id, "status": self.status["status"],
            "reportDirectory": str(self.reports.relative_to(self.root)),
            "statusFile": str((self.reports / "run-status.json").relative_to(self.root)),
        })

    def passed(self, name):
        self.status["completedChecks"].append(name)
        self.publish()

    def finish(self, error=None):
        self.status["status"] = "passed" if error is None else "failed"
        self.status["finishedAt"] = datetime.now(timezone.utc).isoformat()
        if error is not None:
            self.status["error"] = str(error)
        self.publish()


def run(configuration, requested_runtime=None):
    inventory = json.loads(subprocess.check_output(["xcrun", "simctl", "list", "--json"], text=True))
    runtime, devices = select_devices(inventory, requested_runtime)
    app = ROOT / f"artifacts/ios/iossimulator-arm64/{configuration}/bin/DvmConsole.iOS/{configuration}/net10.0-ios/iossimulator-arm64/DvmConsole.iOS.app"
    if not app.is_dir():
        raise RuntimeError(f"Build the simulator app first: {app}")
    candidate = ROOT / f"artifacts/ios/iossimulator-arm64/{configuration}/build-manifest.json"
    evidence = RunEvidence(configuration, runtime, candidate)
    try:
        run_checks(configuration, runtime, devices, app, candidate, evidence)
    except BaseException as error:
        evidence.finish(error)
        raise
    evidence.finish()


def run_checks(configuration, runtime, devices, app, candidate, evidence):
    verify = [sys.executable, str(ROOT / "scripts/write-ios-build-manifest.py"), str(app),
              "--verify-manifest", str(candidate)]
    subprocess.run(verify, check=True)
    reports = evidence.reports
    reference = reports / "desktop-vocoder.txt"
    reference.unlink(missing_ok=True)
    with (reports / "desktop-vocoder.log").open("w") as output:
        subprocess.run(["dotnet", "test", str(ROOT / "src/DvmConsole.Vocoder.Tests/DvmConsole.Vocoder.Tests.csproj"),
                        "-c", configuration, "--filter", "FullyQualifiedName~HostQualificationExercisesAllCodecEntryPoints",
                        "--disable-build-servers", "/m:1", "/p:UseSharedCompilation=false", "/p:RestoreLockedMode=true"],
                       env=dict(os.environ, RUSTUP_TOOLCHAIN="1.85.0", DVM_VOCODER_QUALIFICATION_REPORT=str(reference)),
                       stdout=output, stderr=subprocess.STDOUT, check=True, timeout=300)
    evidence.passed("desktop-vocoder-reference")
    for family, device_type in devices:
        device = subprocess.check_output(["xcrun", "simctl", "create", f"Console NEO smoke {family}", device_type, runtime], text=True).strip()
        try:
            subprocess.run(["xcrun", "simctl", "boot", device], check=True)
            subprocess.run(["xcrun", "simctl", "bootstatus", device, "-b"], check=True, timeout=180)
            subprocess.run(["xcrun", "simctl", "install", device, str(app)], check=True)
            for check in ("vocoder", "audio", "recording", "session", "studio", "connection", "network", "transmit", "routes", "help", "accessibility"):
                with (reports / f"{family.lower()}-{check}.log").open("w") as output:
                    subprocess.run([sys.executable, str(ROOT / "scripts/test-ios-native.py"), device,
                                    "--configuration", configuration, "--check", check] +
                                   (["--compare", str(reference)] if check == "vocoder" else []),
                                   stdout=output, stderr=subprocess.STDOUT, check=True, timeout=90)
                evidence.passed(f"{family.lower()}-{check}")
                print(f"{family}: {check} passed", flush=True)
            # These diagnostics exercise both permission branches without capture.
            # Only this run's temporary devices receive explicit privacy choices.
            for permission, action in (("granted", "grant"), ("denied", "revoke")):
                label = f"inputs-{permission}"
                with (reports / f"{family.lower()}-{label}.log").open("w") as output:
                    subprocess.run(["xcrun", "simctl", "privacy", device, action, "microphone",
                                    "io.jchang.dvmconsole.neo"],
                                   stdout=output, stderr=subprocess.STDOUT, check=True, timeout=30)
                    subprocess.run([sys.executable, str(ROOT / "scripts/test-ios-native.py"), device,
                                    "--configuration", configuration, "--check", "inputs"],
                                   stdout=output, stderr=subprocess.STDOUT, check=True, timeout=90)
                evidence.passed(f"{family.lower()}-{label}")
                print(f"{family}: {label} passed", flush=True)
            for protocol, secure in (("p25", False), ("nxdn", False), ("p25", True)):
                label = f"network-{protocol}" + ("-secure" if secure else "")
                with (reports / f"{family.lower()}-{label}.log").open("w") as output:
                    subprocess.run([sys.executable, str(ROOT / "scripts/test-ios-native.py"), device,
                                    "--configuration", configuration, "--check", "network", "--protocol", protocol] + (["--secure"] if secure else []),
                                   stdout=output, stderr=subprocess.STDOUT, check=True, timeout=90)
                evidence.passed(f"{family.lower()}-{label}")
                print(f"{family}: {label} passed", flush=True)
        finally:
            subprocess.run(["xcrun", "simctl", "shutdown", device], check=False)
            subprocess.run(["xcrun", "simctl", "delete", device], check=True)
    subprocess.run(verify, check=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Release")
    parser.add_argument("--runtime", help="Installed iOS runtime version or identifier; defaults to newest available")
    args = parser.parse_args()
    run(args.configuration, args.runtime)
