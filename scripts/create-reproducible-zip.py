#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Create a deterministic ZIP from one staged package directory."""

from __future__ import annotations

import argparse
import datetime as dt
import os
import pathlib
import stat
import zipfile


ZIP_EPOCH = 315532800  # 1980-01-01T00:00:00Z, the earliest ZIP timestamp.


def normalized_timestamp(source_date_epoch: int) -> tuple[int, int, int, int, int, int]:
    timestamp = max(source_date_epoch, ZIP_EPOCH)
    instant = dt.datetime.fromtimestamp(timestamp, tz=dt.timezone.utc)
    # The DOS timestamp stored by ZIP has two-second precision.
    return (
        instant.year,
        instant.month,
        instant.day,
        instant.hour,
        instant.minute,
        instant.second - instant.second % 2,
    )


def create_archive(
    source_root: pathlib.Path,
    archive: pathlib.Path,
    source_date_epoch: int,
) -> None:
    source_root = source_root.resolve(strict=True)
    if not source_root.is_dir() or source_root.is_symlink():
        raise ValueError("source root must be a regular directory")
    archive = archive.resolve(strict=False)
    if source_root == archive or source_root in archive.parents:
        raise ValueError("archive must remain outside the source tree")

    timestamp = normalized_timestamp(source_date_epoch)
    entries = sorted(source_root.rglob("*"), key=lambda path: path.relative_to(source_root).as_posix())
    with zipfile.ZipFile(
        archive,
        mode="w",
        compression=zipfile.ZIP_DEFLATED,
        compresslevel=9,
        strict_timestamps=True,
    ) as package:
        for path in entries:
            if path.is_symlink():
                raise ValueError(f"source tree contains a symbolic link: {path}")
            relative = pathlib.PurePosixPath(source_root.name) / path.relative_to(source_root)
            name = relative.as_posix()
            if path.is_dir():
                name += "/"
                mode = stat.S_IFDIR | 0o755
                content = b""
            elif path.is_file():
                source_mode = path.stat().st_mode
                mode = stat.S_IFREG | (0o755 if source_mode & 0o111 else 0o644)
                content = path.read_bytes()
            else:
                raise ValueError(f"source tree contains an unsupported entry: {path}")

            info = zipfile.ZipInfo(name, timestamp)
            info.create_system = 3
            info.external_attr = mode << 16
            info.compress_type = zipfile.ZIP_DEFLATED
            package.writestr(info, content, compresslevel=9)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--source-root", required=True, type=pathlib.Path)
    parser.add_argument("--archive", required=True, type=pathlib.Path)
    parser.add_argument(
        "--source-date-epoch",
        type=int,
        default=int(os.environ.get("SOURCE_DATE_EPOCH", ZIP_EPOCH)),
    )
    args = parser.parse_args()
    try:
        create_archive(args.source_root, args.archive, args.source_date_epoch)
    except (OSError, ValueError) as error:
        parser.error(str(error))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
