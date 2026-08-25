#!/usr/bin/env python3
"""Validate the release artifacts produced by the packaging workflow."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable


TARGETS = ("osx-arm64", "osx-x64", "win-x64")
SEMVER_IDENTIFIER = r"(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)"
VERSION_PATTERN = re.compile(
    rf"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    rf"(?:-({SEMVER_IDENTIFIER}(?:\.{SEMVER_IDENTIFIER})*))?$"
)


class MetadataError(ValueError):
    """Raised when a release artifact does not satisfy its contract."""


def semantic_version_core(version: str) -> str:
    match = VERSION_PATTERN.fullmatch(version)
    if match is None:
        raise MetadataError(f"Invalid release version: {version}.")
    return ".".join(match.groups()[:3])


def require_file(path: Path) -> Path:
    if not path.is_file() or path.stat().st_size <= 0:
        raise MetadataError(f"Required non-empty release asset is missing: {path}.")
    return path


def validate_release_notes(path: Path, *, version: str) -> str:
    semantic_version_core(version)
    require_file(path)
    text = path.read_text(encoding="utf-8")
    first_line = text.splitlines()[0] if text.splitlines() else ""
    title_match = re.fullmatch(
        rf"# (DVM Console NEO {re.escape(version)}(?: — .+)?)",
        first_line,
    )
    if title_match is None:
        raise MetadataError(
            f"Release notes must begin with '# DVM Console NEO {version}' "
            "and may include an em-dash outcome."
        )

    if "-" not in version:
        draft_markers = {
            "draft label": r"(?i)(?:\*\*)?draft(?:\*\*)?\s*:",
            "unapproved publication marker": r"(?i)\bnot yet approved\b",
            "template maintainer note": r"(?i)\bmaintainer note\b",
            "template removal instruction": r"(?i)\bremove all instructional text\b",
            "version placeholder": r"\bX\.Y\.Z\b",
            "date placeholder": r"\bYYYY-MM-DD\b",
            "TBD marker": r"(?i)\bTBD\b",
            "TODO marker": r"(?i)\bTODO\b",
        }
        for description, pattern in draft_markers.items():
            if re.search(pattern, text):
                raise MetadataError(
                    f"Stable release notes contain a {description}; "
                    "complete the notes before publication."
                )

    return title_match.group(1)


def read_json(path: Path) -> dict[str, Any]:
    try:
        value = json.loads(path.read_text(encoding="utf-8"))
    except FileNotFoundError as error:
        raise MetadataError(f"Required JSON file does not exist: {path}") from error
    except json.JSONDecodeError as error:
        raise MetadataError(f"Invalid JSON in {path}: {error}") from error

    if not isinstance(value, dict):
        raise MetadataError(f"Expected a JSON object in {path}.")
    return value


def require_nonempty_string(value: Any, context: str) -> str:
    if not isinstance(value, str) or not value.strip():
        raise MetadataError(f"{context} must be a non-empty string.")
    return value


def validate_spdx(path: Path) -> None:
    value = read_json(path)
    if value.get("spdxVersion") not in {"SPDX-2.2", "SPDX-2.3"}:
        raise MetadataError(f"{path.name} is not an SPDX 2.2 or 2.3 JSON document.")
    require_nonempty_string(value.get("name"), f"{path.name}.name")
    require_nonempty_string(value.get("documentNamespace"), f"{path.name}.documentNamespace")
    creation_info = value.get("creationInfo")
    if not isinstance(creation_info, dict) or not creation_info.get("creators"):
        raise MetadataError(f"{path.name} does not identify an SBOM creator.")
    if not any(isinstance(value.get(field), list) and value[field] for field in ("packages", "files")):
        raise MetadataError(f"{path.name} does not inventory any packages or files.")


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def create_checksums(paths: Iterable[Path], output: Path) -> None:
    assets = sorted((require_file(path) for path in paths), key=lambda path: path.name)
    names = [path.name for path in assets]
    if len(names) != len(set(names)):
        raise MetadataError("Checksum subjects must have unique file names.")
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text("".join(f"{sha256(path)}  {path.name}\n" for path in assets), encoding="utf-8")


def verify_checksums(
    checksums: Path,
    artifacts: Path,
    *,
    expected_subjects: Iterable[str] | None = None,
) -> None:
    require_file(checksums)
    lines = checksums.read_text(encoding="utf-8").splitlines()
    if not lines:
        raise MetadataError("SHA256SUMS is empty.")

    seen: set[str] = set()
    ordered_names: list[str] = []
    for line_number, line in enumerate(lines, start=1):
        match = re.fullmatch(r"([0-9a-f]{64})  ([^/\\]+)", line)
        if not match:
            raise MetadataError(f"Invalid SHA256SUMS line {line_number}.")
        expected, name = match.groups()
        if name in seen:
            raise MetadataError(f"Duplicate checksum subject: {name}.")
        seen.add(name)
        ordered_names.append(name)
        if sha256(require_file(artifacts / name)) != expected:
            raise MetadataError(f"Checksum mismatch for {name}.")

    if ordered_names != sorted(ordered_names):
        raise MetadataError("SHA256SUMS subjects must be sorted by file name.")
    if expected_subjects is None:
        return

    expected_names = list(expected_subjects)
    if any(not re.fullmatch(r"[^/\\]+", name) for name in expected_names):
        raise MetadataError("Expected checksum subjects must be plain file names.")
    if len(expected_names) != len(set(expected_names)):
        raise MetadataError("Expected checksum subjects must be unique.")

    expected_set = set(expected_names)
    if seen == expected_set:
        return
    missing = sorted(expected_set - seen)
    unexpected = sorted(seen - expected_set)
    details: list[str] = []
    if missing:
        details.append(f"missing: {', '.join(missing)}")
    if unexpected:
        details.append(f"unexpected: {', '.join(unexpected)}")
    raise MetadataError(
        f"SHA256SUMS subjects do not match the expected release suite ({'; '.join(details)})."
    )


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="command", required=True)

    notes_parser = subparsers.add_parser("validate-notes")
    notes_parser.add_argument("--notes", type=Path, required=True)
    notes_parser.add_argument("--version", required=True)

    version_parser = subparsers.add_parser("version-core")
    version_parser.add_argument("--version", required=True)

    sbom_parser = subparsers.add_parser("validate-sbom")
    sbom_parser.add_argument("--sbom", type=Path, required=True)

    checksums_parser = subparsers.add_parser("create-checksums")
    checksums_parser.add_argument("--output", type=Path, required=True)
    checksums_parser.add_argument("assets", type=Path, nargs="+")

    verify_parser = subparsers.add_parser("verify-checksums")
    verify_parser.add_argument("--checksums", type=Path, required=True)
    verify_parser.add_argument("--artifacts", type=Path, required=True)
    verify_parser.add_argument("--expected-subject", action="append")
    return parser.parse_args(argv)


def main(argv: list[str] | None = None) -> int:
    args = parse_args(argv or sys.argv[1:])
    try:
        if args.command == "validate-notes":
            print(validate_release_notes(args.notes, version=args.version))
        elif args.command == "version-core":
            print(semantic_version_core(args.version))
        elif args.command == "validate-sbom":
            validate_spdx(args.sbom)
        elif args.command == "create-checksums":
            create_checksums(args.assets, args.output)
        elif args.command == "verify-checksums":
            verify_checksums(
                args.checksums,
                args.artifacts,
                expected_subjects=args.expected_subject,
            )
        else:
            raise MetadataError(f"Unsupported command: {args.command}.")
    except MetadataError as error:
        print(f"release metadata error: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
