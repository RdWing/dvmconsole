#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

from __future__ import annotations

import argparse
from pathlib import Path


MIB = 1024 * 1024
ABSOLUTE_BUDGETS = {
    "osx-arm64": 30 * MIB,
    "osx-x64": 30 * MIB,
    "win-x64": 25 * MIB,
    "win-arm64": 25 * MIB,
    "linux-x64": 25 * MIB,
    "linux-arm64": 25 * MIB,
}


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--rid", choices=sorted(ABSOLUTE_BUDGETS), required=True)
    parser.add_argument("--package", type=Path, required=True)
    args = parser.parse_args()

    package = args.package.resolve()
    if not package.is_file():
        parser.error(f"package does not exist: {package}")

    size = package.stat().st_size
    absolute = ABSOLUTE_BUDGETS[args.rid]
    if size > absolute:
        raise SystemExit(
            f"{package.name} is {size} bytes and exceeds the hard {absolute}-byte budget."
        )

    print(f"Package size verification passed: {package.name} ({args.rid}, {size} bytes, limit {absolute})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
