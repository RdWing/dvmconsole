#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Describe a local iOS build without writing into its application bundle."""

import argparse
import hashlib
import json
from pathlib import Path
import plistlib
import subprocess



def capture_source(root):
    """Hash Git-visible files, including dirty/untracked inputs and submodules.

    Ignored build products and local secrets stay excluded. Only the digest and
    counts enter the manifest, never source contents or untracked filenames.
    """
    digest = hashlib.sha256()
    count = 0
    submodules = {}

    def visit(repository, prefix=""):
        nonlocal count
        paths = subprocess.check_output(
            ["git", "ls-files", "--cached", "--others", "--exclude-standard", "-z"], cwd=repository)
        for name in sorted(set(paths.split(b"\0")) - {b""}):
            relative = name.decode("utf-8", errors="surrogateescape")
            path = repository / relative
            label = prefix + relative
            if path.is_symlink():
                value = {"link": str(path.readlink())}
            elif path.is_dir():
                revision = subprocess.check_output(
                    ["git", "rev-parse", "HEAD"], cwd=path, text=True).strip()
                submodules[label] = revision
                visit(path, label + "/")
                value = {"submodule": revision}
            elif path.is_file():
                with path.open("rb") as source:
                    value = {"sha256": hashlib.file_digest(source, "sha256").hexdigest(),
                             "executable": bool(path.stat().st_mode & 0o111)}
            else:
                value = {"missing": True}
            digest.update(json.dumps([label, value], sort_keys=True, ensure_ascii=True).encode() + b"\n")
            count += 1

    visit(root)
    return {
        "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip(),
        "hasLocalChanges": bool(subprocess.check_output(["git", "status", "--porcelain"], cwd=root).strip()),
        "treeSha256": digest.hexdigest(),
        "fileCount": count,
        "submodules": submodules,
    }


def checked_source(root, snapshot_path):
    current = capture_source(root)
    if snapshot_path is None:
        return current
    expected = json.loads(snapshot_path.read_text())
    if current != expected:
        raise RuntimeError("Source changed during the iOS build; rebuild before qualifying this candidate.")
    return expected


def capture_bundle(app):
    entries = {}
    for path in sorted(app.rglob("*")):
        if path.is_symlink():
            entries[path.relative_to(app).as_posix()] = {"link": str(path.readlink())}
        elif path.is_file():
            with path.open("rb") as source:
                entries[path.relative_to(app).as_posix()] = {"sha256": hashlib.file_digest(source, "sha256").hexdigest()}
    encoded_entries = json.dumps(entries, sort_keys=True, separators=(",", ":")).encode()
    return entries, hashlib.sha256(encoded_entries).hexdigest()


def verify_build(root, app, manifest_path):
    candidate = json.loads(manifest_path.read_text())
    if not candidate.get("sourceVerifiedUnchangedDuringBuild") or capture_source(root) != candidate.get("sourceSnapshot"):
        raise RuntimeError("The iOS candidate does not match current source; rebuild before qualification.")
    if capture_bundle(app)[1] != candidate["bundleManifestSha256"]:
        raise RuntimeError("The iOS app bundle differs from its build manifest; rebuild before qualification.")


def encryption_declaration(info):
    value = info.get("ITSAppUsesNonExemptEncryption")
    if type(value) is not bool:
        raise RuntimeError("Info.plist must declare ITSAppUsesNonExemptEncryption as a Boolean.")
    return value


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("app", type=Path, nargs="?")
    parser.add_argument("--configuration", choices=("Debug", "Release"))
    parser.add_argument("--output", type=Path)
    parser.add_argument("--capture-source", type=Path)
    parser.add_argument("--source-snapshot", type=Path)
    parser.add_argument("--verify-manifest", type=Path)
    parser.add_argument("--runtime", choices=("iossimulator-arm64", "ios-arm64"), default="iossimulator-arm64")
    parser.add_argument("--mode", choices=("simulator", "unsigned-device", "signed-private-device", "app-store"), default="simulator")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    if args.capture_source:
        args.capture_source.write_text(json.dumps(capture_source(root), sort_keys=True) + "\n")
        return
    if args.verify_manifest:
        if args.app is None:
            parser.error("app is required with --verify-manifest")
        verify_build(root, args.app.resolve(), args.verify_manifest)
        return
    if args.app is None or args.configuration is None or args.output is None:
        parser.error("app, --configuration and --output are required when writing a build manifest")
    source_snapshot = checked_source(root, args.source_snapshot)
    if (args.mode == "simulator") != (args.runtime == "iossimulator-arm64"):
        parser.error("The build mode and runtime do not match")
    app = args.app.resolve()
    with (app / "Info.plist").open("rb") as source:
        info = plistlib.load(source)
    entries, bundle_digest = capture_bundle(app)
    manifest = {
        "sourceRevision": source_snapshot["revision"],
        "sourceHasLocalChanges": source_snapshot["hasLocalChanges"],
        "sourceSnapshot": source_snapshot,
        "sourceVerifiedUnchangedDuringBuild": args.source_snapshot is not None,
        "configuration": args.configuration,
        "runtime": args.runtime,
        "mode": args.mode,
        "architecture": "arm64",
        "bundleId": info["CFBundleIdentifier"],
        "buildNumber": info["CFBundleVersion"],
        "usesNonExemptEncryption": encryption_declaration(info),
        "minimumOSVersion": info["MinimumOSVersion"],
        "bundleManifestSha256": bundle_digest,
        "files": entries,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n")
    print(f"Build manifest: {args.output}")


if __name__ == "__main__":
    main()
