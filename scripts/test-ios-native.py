#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Install a built simulator app and run its explicit native qualification checks."""

import argparse
import os
from pathlib import Path
import subprocess
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("simulator", help="UUID of a booted iOS simulator")
    parser.add_argument("--configuration", choices=("Debug", "Release"), default="Debug")
    parser.add_argument("--check", choices=("background", "vocoder", "audio", "recording", "session", "studio", "connection", "network", "transmit", "routes", "help", "inputs", "accessibility"), default="vocoder")
    parser.add_argument("--secure", action="store_true", help="Network P25 check only: exercise AES-256 with synthetic qualification keys")
    parser.add_argument("--protocol", choices=("dmr", "p25", "nxdn"), default="dmr", help="Network check media protocol")
    parser.add_argument("--screenshot", type=Path, help="Studio check only: capture the native editor within the settings shell")
    parser.add_argument("--studio-section", choices=("overview", "systems", "zones", "streams", "groups", "encryptionkeys", "files"), default="overview")
    parser.add_argument("--compare", type=Path, help="Compare with a previously captured desktop or simulator report")
    parser.add_argument("--quiet-seconds", type=int, default=0,
                        help="Session/network/background checks: wait before injecting traffic, allowing manual background/lock testing (0-300)")
    parser.add_argument("--inspect-seconds", type=int, default=0,
                        help="Session/network/routes checks: retain the player or route picker for system UI inspection (0-300)")
    parser.add_argument("--muted", action="store_true", help="Session/network checks only: keep local channel output at zero gain")
    args = parser.parse_args()
    if not 0 <= args.quiet_seconds <= 300 or (args.quiet_seconds and args.check not in ("session", "network", "background")):
        parser.error("--quiet-seconds requires --check session, network or background and a value from 0 to 300")
    if not 0 <= args.inspect_seconds <= 300 or (args.inspect_seconds and args.check not in ("session", "network", "routes")):
        parser.error("--inspect-seconds requires --check session, network or routes and a value from 0 to 300")
    if args.check == "background" and args.quiet_seconds == 0:
        parser.error("--check background requires --quiet-seconds from 1 to 300")
    if args.muted and args.check not in ("session", "network"):
        parser.error("--muted requires --check session or network")
    if args.protocol != "dmr" and args.check != "network":
        parser.error("--protocol requires --check network")
    if args.screenshot and args.check != "studio":
        parser.error("--screenshot requires --check studio")
    if args.studio_section != "overview" and not args.screenshot:
        parser.error("--studio-section requires --screenshot")
    if args.secure and (args.check != "network" or args.protocol != "p25"):
        parser.error("--secure requires --check network --protocol p25")
    root = Path(__file__).resolve().parents[1]
    app = root / "artifacts/ios/iossimulator-arm64" / args.configuration / "bin/DvmConsole.iOS" / args.configuration / "net10.0-ios/iossimulator-arm64/DvmConsole.iOS.app"
    if not app.is_dir():
        parser.error("Build the simulator application with scripts/build-ios.sh first.")
    bundle = "io.jchang.dvmconsole.neo"
    subprocess.run(["xcrun", "simctl", "install", args.simulator, str(app)], check=True)
    container = subprocess.check_output(
        ["xcrun", "simctl", "get_app_container", args.simulator, bundle, "data"], text=True
    ).strip()
    report = Path(container) / "Documents" / f"{args.check}-smoke.txt"
    # A previous PASS must never satisfy a new run.
    report.unlink(missing_ok=True)
    progress = Path(container) / "Documents" / "session-progress.txt"
    progress.unlink(missing_ok=True)
    ready = Path(container) / "Documents" / "session-quiet-ready.txt"
    ready.unlink(missing_ok=True)
    inspect_ready = Path(container) / "Documents" / "session-inspect-ready.txt"
    inspect_ready.unlink(missing_ok=True)
    studio_ready = Path(container) / "Documents" / "studio-ui-ready.txt"
    studio_captured = Path(container) / "Documents" / "studio-ui-captured.txt"
    studio_ready.unlink(missing_ok=True)
    studio_captured.unlink(missing_ok=True)
    subprocess.run(
        ["xcrun", "simctl", "launch", "--terminate-running-process", args.simulator, bundle],
        env=dict(os.environ, **{f"SIMCTL_CHILD_DVM_{args.check.upper()}_SMOKE": "1",
                                "SIMCTL_CHILD_DVM_STUDIO_SCREENSHOT": "1" if args.screenshot else "0",
                                "SIMCTL_CHILD_DVM_STUDIO_SECTION": args.studio_section,
                                "SIMCTL_CHILD_DVM_NETWORK_SECURE": "1" if args.secure else "0",
                                "SIMCTL_CHILD_DVM_NETWORK_PROTOCOL": args.protocol,
                                "SIMCTL_CHILD_DVM_SESSION_QUIET_SECONDS": str(args.quiet_seconds),
                                "SIMCTL_CHILD_DVM_SESSION_INSPECT_SECONDS": str(args.inspect_seconds),
                                "SIMCTL_CHILD_DVM_SESSION_MUTED": "1" if args.muted else "0"}), check=True, stdout=subprocess.DEVNULL
    )
    timeout = (60 if args.check in ("connection", "help") else 30) + args.quiet_seconds + args.inspect_seconds
    deadline = time.monotonic() + timeout
    announced = False
    inspection_announced = False
    while time.monotonic() < deadline:
        if args.screenshot and studio_ready.exists() and not studio_captured.exists():
            args.screenshot.parent.mkdir(parents=True, exist_ok=True)
            subprocess.run(["xcrun", "simctl", "io", args.simulator, "screenshot", str(args.screenshot)],
                           check=True, timeout=10, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            studio_captured.write_text("Screenshot captured")
        if args.quiet_seconds and ready.exists() and not announced:
            print(ready.read_text() + " Put the simulator app in the background now.", flush=True)
            announced = True
        if args.inspect_seconds and inspect_ready.exists() and not inspection_announced:
            print(inspect_ready.read_text(), flush=True)
            inspection_announced = True
        if report.exists():
            result = report.read_text()
            lines = result.strip().splitlines()
            if lines and lines[0] == "FAIL":
                raise SystemExit(result)
            if len(lines) == (5 if args.check == "vocoder" else 2) and lines[0] == "PASS":
                if args.compare and result.strip() != args.compare.read_text().strip():
                    raise SystemExit("Native results differ from the comparison report:\n" + result)
                print(result)
                return
        time.sleep(0.25)
    last_stage = f" Last stage: {progress.read_text().strip()}." if args.check in ("session", "network") and progress.exists() else ""
    raise SystemExit(f"The simulator did not produce a complete native report within {timeout} seconds.{last_stage}")


if __name__ == "__main__":
    main()
