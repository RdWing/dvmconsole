#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Validate the packaged documentation manifest and every local image link."""

from __future__ import annotations

import argparse
import binascii
import json
import re
import struct
import zlib
from pathlib import Path

IMAGE_LINK = re.compile(r"!\[[^\]]*\]\(([^)]+)\)")
PNG_SIGNATURE = b"\x89PNG\r\n\x1a\n"
PNG_CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


def normalized_relative(root: Path, value: str) -> Path:
    candidate = (root / value).resolve()
    try:
        return candidate.relative_to(root.resolve())
    except ValueError as error:
        raise ValueError(f"documentation path escapes its root: {value}") from error


def validate_png(path: Path) -> None:
    encoded = path.read_bytes()
    if not encoded.startswith(PNG_SIGNATURE):
        raise ValueError(f"documentation image is not a PNG: {path.name}")

    offset = len(PNG_SIGNATURE)
    header: tuple[int, int, int, int] | None = None
    compressed = bytearray()
    saw_end = False
    while offset < len(encoded):
        if len(encoded) - offset < 12:
            raise ValueError(f"documentation PNG has a truncated chunk: {path.name}")
        length = struct.unpack_from(">I", encoded, offset)[0]
        chunk_end = offset + 12 + length
        if chunk_end > len(encoded):
            raise ValueError(f"documentation PNG has a truncated chunk: {path.name}")
        chunk_type = encoded[offset + 4 : offset + 8]
        payload = encoded[offset + 8 : offset + 8 + length]
        expected_crc = struct.unpack_from(">I", encoded, offset + 8 + length)[0]
        actual_crc = binascii.crc32(chunk_type + payload) & 0xFFFFFFFF
        if actual_crc != expected_crc:
            raise ValueError(f"documentation PNG has an invalid CRC: {path.name}")

        if chunk_type == b"IHDR":
            if header is not None or length != 13:
                raise ValueError(f"documentation PNG has an invalid header: {path.name}")
            width, height, bit_depth, color_type, compression, filtering, interlace = struct.unpack(
                ">IIBBBBB", payload
            )
            if width == 0 or height == 0 or width > 16_384 or height > 16_384:
                raise ValueError(f"documentation PNG dimensions are invalid: {path.name}")
            if color_type not in PNG_CHANNELS or bit_depth not in {1, 2, 4, 8, 16}:
                raise ValueError(f"documentation PNG pixel format is unsupported: {path.name}")
            if color_type in {2, 4, 6} and bit_depth not in {8, 16}:
                raise ValueError(f"documentation PNG pixel format is invalid: {path.name}")
            if compression != 0 or filtering != 0 or interlace != 0:
                raise ValueError(f"documentation PNG encoding is unsupported: {path.name}")
            header = width, height, bit_depth, color_type
        elif chunk_type == b"IDAT":
            compressed.extend(payload)
        elif chunk_type == b"IEND":
            if length != 0:
                raise ValueError(f"documentation PNG has an invalid end chunk: {path.name}")
            saw_end = True
            offset = chunk_end
            break
        offset = chunk_end

    if header is None or not compressed or not saw_end or offset != len(encoded):
        raise ValueError(f"documentation PNG is incomplete: {path.name}")

    width, height, bit_depth, color_type = header
    row_bytes = (width * PNG_CHANNELS[color_type] * bit_depth + 7) // 8
    expected_length = height * (row_bytes + 1)
    try:
        pixels = zlib.decompress(compressed)
    except zlib.error as error:
        raise ValueError(f"documentation PNG pixel data is corrupt: {path.name}") from error
    if len(pixels) != expected_length:
        raise ValueError(f"documentation PNG pixel data has the wrong size: {path.name}")
    if any(pixels[row * (row_bytes + 1)] > 4 for row in range(height)):
        raise ValueError(f"documentation PNG uses an invalid row filter: {path.name}")


def validate(root: Path) -> None:
    manifest_path = root / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    pages = {normalized_relative(root, value).as_posix() for value in manifest["pages"]}
    assets = {normalized_relative(root, value).as_posix() for value in manifest["assets"]}
    if not pages or len(pages) != len(manifest["pages"]):
        raise ValueError("documentation manifest pages must be unique and non-empty")
    if len(assets) != len(manifest["assets"]):
        raise ValueError("documentation manifest assets must be unique")

    for relative in sorted(pages | assets):
        if not (root / relative).is_file():
            raise ValueError(f"manifest entry is missing: {relative}")
    for relative in sorted(assets):
        validate_png(root / relative)

    referenced_assets: set[str] = set()
    for page in sorted(pages):
        markdown = (root / page).read_text(encoding="utf-8")
        for link in IMAGE_LINK.findall(markdown):
            if "://" in link or link.startswith("data:"):
                continue
            resolved = normalized_relative(root, (Path(page).parent / link).as_posix()).as_posix()
            if resolved not in assets:
                raise ValueError(f"{page} references undeclared local image: {link}")
            referenced_assets.add(resolved)

    unreferenced = assets - referenced_assets
    if unreferenced:
        raise ValueError("unreferenced documentation assets: " + ", ".join(sorted(unreferenced)))


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    arguments = parser.parse_args()
    validate(arguments.root.resolve())
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
