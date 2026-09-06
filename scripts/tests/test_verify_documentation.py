# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

import binascii
import importlib.util
import json
import pathlib
import struct
import tempfile
import unittest
import zlib


REPOSITORY = pathlib.Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location(
    "verify_documentation", REPOSITORY / "scripts" / "verify-documentation.py"
)
VERIFY_DOCUMENTATION = importlib.util.module_from_spec(SPEC)
assert SPEC.loader is not None
SPEC.loader.exec_module(VERIFY_DOCUMENTATION)


def png_chunk(kind: bytes, payload: bytes) -> bytes:
    checksum = binascii.crc32(kind + payload) & 0xFFFFFFFF
    return struct.pack(">I", len(payload)) + kind + payload + struct.pack(">I", checksum)


def one_pixel_png() -> bytes:
    header = struct.pack(">IIBBBBB", 1, 1, 8, 6, 0, 0, 0)
    pixels = zlib.compress(b"\x00\xff\x00\x00\xff")
    return (
        VERIFY_DOCUMENTATION.PNG_SIGNATURE
        + png_chunk(b"IHDR", header)
        + png_chunk(b"IDAT", pixels)
        + png_chunk(b"IEND", b"")
    )


class VerifyDocumentationTests(unittest.TestCase):
    def test_decodes_every_declared_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "Assets").mkdir()
            (root / "guide.md").write_text("![screen](Assets/screen.png)", encoding="utf-8")
            (root / "Assets" / "screen.png").write_bytes(one_pixel_png())
            (root / "manifest.json").write_text(
                json.dumps({"pages": ["guide.md"], "assets": ["Assets/screen.png"]}),
                encoding="utf-8",
            )

            VERIFY_DOCUMENTATION.validate(root)

    def test_rejects_corrupt_declared_image(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = pathlib.Path(temporary)
            (root / "Assets").mkdir()
            (root / "guide.md").write_text("![screen](Assets/screen.png)", encoding="utf-8")
            corrupted = bytearray(one_pixel_png())
            corrupted[-5] ^= 0x01
            (root / "Assets" / "screen.png").write_bytes(corrupted)
            (root / "manifest.json").write_text(
                json.dumps({"pages": ["guide.md"], "assets": ["Assets/screen.png"]}),
                encoding="utf-8",
            )

            with self.assertRaisesRegex(ValueError, "CRC"):
                VERIFY_DOCUMENTATION.validate(root)


if __name__ == "__main__":
    unittest.main()
