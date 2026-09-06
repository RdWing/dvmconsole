#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Validate shared publish rules and compare desktop ZIPs with their staged inventory."""

from __future__ import annotations

import argparse
import pathlib
import stat
import zipfile


def expected_files(publish_root: pathlib.Path, rid: str) -> set[str]:
    published = {
        path.relative_to(publish_root).as_posix()
        for path in publish_root.rglob("*")
        if path.is_file()
    }
    if rid.startswith("osx-"):
        root = "DVMConsole.app"
        mapped = {
            "DVM Console" if relative == "DvmConsole" else relative
            for relative in published
        }
        return {
            f"{root}/Contents/Info.plist",
            f"{root}/Contents/Resources/DVMConsole.icns",
            *(f"{root}/Contents/MacOS/{relative}" for relative in mapped),
        }
    return {f"DVMConsole-{rid}/{relative}" for relative in published}


def verify_archive(
    archive: pathlib.Path,
    publish_root: pathlib.Path,
    rid: str,
    staged_root: pathlib.Path | None = None,
) -> None:
    expected = expected_files(publish_root, rid)
    staged_root = staged_root.resolve(strict=True) if staged_root is not None else None
    if staged_root is not None and not staged_root.is_dir():
        raise ValueError("staged package root must be a directory")
    actual: set[str] = set()
    with zipfile.ZipFile(archive) as package:
        seen: set[str] = set()
        for entry in package.infolist():
            name = entry.filename
            normalized = pathlib.PurePosixPath(name)
            if name in seen:
                raise ValueError(f"package contains duplicate entry: {name}")
            seen.add(name)
            if name.startswith("/") or "\\" in name or ".." in normalized.parts:
                raise ValueError(f"package contains unsafe entry: {name}")
            mode = entry.external_attr >> 16
            if stat.S_ISLNK(mode):
                raise ValueError(f"package contains symbolic link: {name}")
            if not entry.is_dir():
                actual.add(name.rstrip("/"))
                if staged_root is not None:
                    parts = normalized.parts
                    if not parts or parts[0] != staged_root.name:
                        raise ValueError(f"package entry is outside its staged root: {name}")
                    staged_file = staged_root.joinpath(*parts[1:])
                    if not staged_file.is_file() or staged_file.is_symlink():
                        raise ValueError(f"package entry has no regular staged source: {name}")
                    if package.read(entry) != staged_file.read_bytes():
                        raise ValueError(f"package content differs from staging: {name}")
                    expected_mode = 0o755 if staged_file.stat().st_mode & 0o111 else 0o644
                    if mode & 0o777 != expected_mode:
                        raise ValueError(
                            f"package mode differs from staging policy: {name}"
                        )

    missing = sorted(expected - actual)
    unexpected = sorted(actual - expected)
    if missing or unexpected:
        details: list[str] = []
        if missing:
            details.append("missing: " + ", ".join(missing[:10]))
        if unexpected:
            details.append("unexpected: " + ", ".join(unexpected[:10]))
        raise ValueError("package inventory mismatch; " + "; ".join(details))


# These operator entry pages are required even if a damaged manifest omits one.
REQUIRED_PAGES = (
    "01-Overview.md", "02-Building.md",
    "03-Configurations/01-Codeplug Creation.md", "03-Configurations/02-Encryption Keys.md",
    "03-Configurations/03-RID Aliases.md", "03-Configurations/04-Groups and Patching.md",
    "03-Configurations/05-Talkgroup Audio Recorder.md", "04-Operations/01-Console Operation.md",
    "04-Operations/02-Settings Reference.md", "04-Operations/03-Audio Settings.md",
    "04-Operations/04-Alert Tones.md",
)


def verify_publish(root: pathlib.Path, rid: str) -> None:
    import importlib.util
    import stat

    if rid not in {"osx-arm64", "osx-x64", "win-x64", "win-arm64", "linux-x64", "linux-arm64"}:
        raise ValueError("unsupported desktop package RID")
    if not root.is_dir():
        raise ValueError(f"Publish directory does not exist: {root}")
    entries = list(root.rglob("*"))
    if any(p.suffix.lower() in {".pdb", ".dsym"} for p in entries):
        raise ValueError("Publish contains debugging symbols")
    metadata = [(p, p.stat()) for p in entries]
    files = [info for _, info in metadata if stat.S_ISREG(info.st_mode)]
    maximum_bytes, maximum_files = (180 * 1024 * 1024, 250) if rid.startswith("win-") else (200 * 1024 * 1024, 800)
    # Cover both logical payload size and allocated storage; sparse files must
    # not evade a budget, and Unix filesystem allocation remains bounded.
    logical = sum(info.st_size for info in files)
    allocated = sum(getattr(info, "st_blocks", 0) * 512 for info in [root.stat(), *(info for _, info in metadata)])
    if max(logical, allocated) > maximum_bytes:
        raise ValueError("Publish exceeds byte size budget")
    if len(files) > maximum_files:
        raise ValueError("Publish exceeds file budget")
    required = ["LICENSE"]
    if rid.startswith("win-"):
        required.append("DvmConsole.exe")
    elif rid.startswith("osx-"):
        required.extend(["DvmConsole.dll", "DvmConsole.deps.json", "DvmConsole.runtimeconfig.json"])
    for name in required:
        if not (root / name).is_file():
            raise ValueError(f"Publish is missing required file: {name}")
    if (root / "Docs").exists():
        raise ValueError("Publish contains obsolete Docs directory")
    documentation = root / "Documentation"
    for page in REQUIRED_PAGES:
        if not (documentation / "Getting Started" / page).is_file():
            raise ValueError(f"Publish is missing documentation: {page}")
    spec = importlib.util.spec_from_file_location("verify_documentation", pathlib.Path(__file__).with_name("verify-documentation.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.validate(documentation)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--publish-only", action="store_true")
    parser.add_argument("--archive", type=pathlib.Path)
    parser.add_argument("--publish-root", required=True, type=pathlib.Path)
    parser.add_argument("--staged-root", type=pathlib.Path)
    parser.add_argument("--rid", required=True)
    args = parser.parse_args()

    if args.publish_only:
        if args.archive or args.staged_root:
            parser.error("--publish-only cannot be combined with archive options")
        verify_publish(args.publish_root.resolve(), args.rid)
        print("Publish inventory verification passed")
        return 0
    if args.archive is None:
        parser.error("--archive is required unless --publish-only is selected")
    if args.rid not in {
        "osx-arm64", "osx-x64", "win-x64", "win-arm64"
    }:
        parser.error("unsupported desktop package RID")
    verify_archive(
        args.archive.resolve(),
        args.publish_root.resolve(),
        args.rid,
        args.staged_root,
    )
    print(f"Package inventory verification passed: {args.archive}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
