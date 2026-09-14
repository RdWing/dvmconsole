#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Sign and notarize a verified macOS ZIP without modifying the unsigned input."""

from __future__ import annotations

import argparse
import json
import os
from pathlib import Path, PurePosixPath
import plistlib
import stat
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parent.parent
MACH_MAGIC = {bytes.fromhex(value) for value in
              ("feedface", "cefaedfe", "feedfacf", "cffaedfe", "cafebabe", "bebafeca", "cafebabf", "bfbafeca")}


def run(*args: str | Path, capture: bool = False) -> str:
    result = subprocess.run([str(arg) for arg in args], check=True, text=True,
                            stdout=subprocess.PIPE if capture else None)
    return result.stdout or ""


def validate_input(archive: Path) -> None:
    """Reject unexpected roots, traversal and symlinks before native extraction."""
    with zipfile.ZipFile(archive) as package:
        seen = set()
        for entry in package.infolist():
            path = PurePosixPath(entry.filename)
            if (path.is_absolute() or ".." in path.parts or "\\" in entry.filename
                    or not path.parts or path.parts[0] != "DVMConsole.app"
                    or entry.filename in seen or stat.S_ISLNK(entry.external_attr >> 16)):
                raise ValueError(f"Unsafe package member: {entry.filename!r}")
            seen.add(entry.filename)
        if "DVMConsole.app/Contents/MacOS/DVM Console" not in seen:
            raise ValueError("Package is missing the console apphost")


def macho_files(app: Path) -> list[Path]:
    binaries = []
    for path in sorted(app.rglob("*")):
        if path.is_symlink():
            raise ValueError("Unexpected symlink in app bundle")
        if path.is_file():
            with path.open("rb") as stream:
                if stream.read(4) in MACH_MAGIC:
                    binaries.append(path)
    return binaries


def notary_auth() -> list[str]:
    profile = os.environ.get("NEO_NOTARY_PROFILE")
    if profile:
        return ["--keychain-profile", profile]
    names = ("ASC_API_KEY_FILE", "ASC_API_KEY_ID", "ASC_API_ISSUER_ID")
    if not all(os.environ.get(name) for name in names):
        raise ValueError("Set NEO_NOTARY_PROFILE or all three ASC_API_KEY_* / issuer inputs")
    return ["--key", os.environ[names[0]], "--key-id", os.environ[names[1]],
            "--issuer", os.environ[names[2]]]


def verify_audio_permission(app: Path, evidence: Path) -> None:
    """Check the signed app, not just the entitlement file passed to codesign."""
    result = subprocess.run(["codesign", "-d", "--entitlements", "-", "--xml", str(app)],
                            check=True, capture_output=True)
    (evidence / "entitlements.plist").write_bytes(result.stdout)
    try:
        entitlements = plistlib.loads(result.stdout)
    except plistlib.InvalidFileException as error:
        raise ValueError("Signed app has no readable XML entitlements") from error
    if entitlements.get("com.apple.security.device.audio-input") is not True:
        raise ValueError("Signed app is missing the Hardened Runtime audio-input entitlement")
    info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
    description = info.get("NSMicrophoneUsageDescription")
    if not isinstance(description, str) or not description.strip():
        raise ValueError("Signed app is missing its microphone permission description")


def submit_notarization(submission: Path, auth: list[str], evidence: Path) -> None:
    result = subprocess.run(["xcrun", "notarytool", "submit", str(submission), *auth,
                             "--wait", "--timeout", "20m", "--output-format", "json"],
                            capture_output=True, text=True)
    (evidence / "submission.json").write_text(result.stdout)
    (evidence / "submission.stderr.txt").write_text(result.stderr)
    (evidence / "submission-result.json").write_text(json.dumps({"exit_code": result.returncode}))
    try:
        status = json.loads(result.stdout)
    except json.JSONDecodeError:
        status = None
    if not isinstance(status, dict):
        detail = result.stderr.strip() or "Apple returned no valid JSON status."
        raise RuntimeError(f"Notarization failed (exit {result.returncode}): {detail} "
                           "The submission outcome is unknown; check Apple before submitting again.")
    request_id = status.get("id")
    has_request_id = isinstance(request_id, str) and bool(request_id.strip())
    if has_request_id:
        # Retrieving diagnostics must not hide the original rejection or timeout.
        log = subprocess.run(["xcrun", "notarytool", "log", request_id, *auth,
                              str(evidence / "notary-log.json")], capture_output=True, text=True)
        (evidence / "notary-log.stderr.txt").write_text(log.stderr)
        (evidence / "notary-log-result.json").write_text(json.dumps({"exit_code": log.returncode}))
    if result.returncode or status.get("status") != "Accepted" or not has_request_id:
        detail = result.stderr.strip() or status.get("message") or "See the saved submission evidence."
        raise RuntimeError(f"Notarization did not complete successfully: {status.get('status', 'unknown')} "
                           f"(exit {result.returncode}, request {request_id or 'unknown'}). {detail} "
                           "Check this request's status before submitting again.")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("archive", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--identity", required=True)
    parser.add_argument("--keychain", type=Path)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    archive = args.archive.resolve(strict=True)
    output = args.output.resolve()
    if output == archive or output.exists() or output.suffix != ".zip":
        raise ValueError("Choose a new ZIP output; never overwrite the unsigned evidence")
    validate_input(archive)
    auth = notary_auth()
    args.evidence.mkdir(parents=True, exist_ok=True)
    output.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="neo-notarize-", dir=output.parent) as temporary:
        staging = Path(temporary)
        run("ditto", "-x", "-k", archive, staging)
        app = staging / "DVMConsole.app"
        info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
        if info.get("CFBundleExecutable") != "DVM Console":
            raise ValueError("Unexpected app executable")
        signing = ["--force", "--sign", args.identity, "--timestamp", "--options", "runtime"]
        if args.keychain:
            signing += ["--keychain", str(args.keychain)]
        # Avalonia's loose .NET assemblies also reside in the code directory.
        # Sign each file there; ditto preserves non-Mach-O extended signatures.
        # Data directories are packaged under Resources instead.
        native = set(macho_files(app))
        for binary in sorted((app / "Contents/MacOS").iterdir()):
            if not binary.is_file():
                raise ValueError("Unexpected directory in executable payload")
            if binary != app / "Contents/MacOS/DVM Console":
                options = signing if binary in native else [arg for arg in signing if arg not in ("--options", "runtime")]
                run("codesign", *options, binary)
        run("codesign", *signing, "--entitlements", ROOT / "packaging/macos/DeveloperID.entitlements", app)
        run("codesign", "--verify", "--deep", "--strict", "--verbose=2", app)
        details = subprocess.run(["codesign", "-dvv", str(app)], check=True,
                                 capture_output=True, text=True).stderr
        if "Authority=Developer ID Application:" not in details or "runtime" not in details:
            raise ValueError("Expected a Developer ID signature with Hardened Runtime")
        (args.evidence / "signature.txt").write_text(details)
        verify_audio_permission(app, args.evidence)
        submission = staging / "submission.zip"
        run("ditto", "-c", "-k", "--keepParent", app, submission)
        submit_notarization(submission, auth, args.evidence)
        run("xcrun", "stapler", "staple", app)
        run("xcrun", "stapler", "validate", app)
        final = staging / "notarized.zip"
        run("ditto", "-c", "-k", "--keepParent", app, final)
        # Gatekeeper must accept the actual distributed ZIP after fresh extraction.
        extracted = staging / "readback"
        run("ditto", "-x", "-k", final, extracted)
        delivered = extracted / "DVMConsole.app"
        run("codesign", "--verify", "--deep", "--strict", delivered)
        verify_audio_permission(delivered, args.evidence)
        run("xcrun", "stapler", "validate", delivered)
        run("spctl", "--assess", "--type", "execute", "--verbose=2", delivered)
        final.replace(output)
        print(f"Signed, notarized and Gatekeeper-verified: {output}")


if __name__ == "__main__":
    main()
