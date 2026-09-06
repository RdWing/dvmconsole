#!/usr/bin/env python3
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Resolve and validate a caller-controlled desktop package destination."""

from __future__ import annotations

import argparse
import pathlib
from collections.abc import Iterable


def is_within(path: pathlib.PurePath, parent: pathlib.PurePath) -> bool:
    try:
        path.relative_to(parent)
        return True
    except ValueError:
        return False


def ancestor_check_boundary(
    path: pathlib.PurePath, roots: Iterable[pathlib.PurePath]
) -> pathlib.PurePath:
    roots = tuple(roots)
    for candidate in (path, *path.parents):
        if all(is_within(root, candidate) for root in roots):
            return candidate
    # Separate Windows drives have no common ancestor. Check every ancestor
    # on the destination drive instead of rejecting a valid external output.
    return type(path)(path.anchor)


def validate_target(
    requested: pathlib.Path,
    extension: str | None,
    repository_root: pathlib.Path,
    publish_root: pathlib.Path | None,
    allow_repository_target: bool,
    staging_roots: Iterable[pathlib.Path] = (),
) -> pathlib.Path:
    expects_directory = extension is None
    if extension is not None and requested.suffix.lower() != extension.lower():
        raise ValueError(f"destination must have the {extension} extension")
    requested_is_junction = getattr(requested, "is_junction", lambda: False)
    if requested.is_symlink() or requested_is_junction():
        raise ValueError("destination must not be a symbolic link or junction")

    unresolved = requested.expanduser().absolute()
    lexical_roots = [repository_root.expanduser().absolute()]
    if publish_root is not None:
        lexical_roots.append(publish_root.expanduser().absolute())
    lexical_roots.extend(root.expanduser().absolute() for root in staging_roots)
    common_root = ancestor_check_boundary(unresolved, lexical_roots)
    for parent in unresolved.parents:
        if parent == common_root or common_root not in parent.parents:
            break
        is_junction = getattr(parent, "is_junction", lambda: False)
        if parent.is_symlink() or is_junction():
            raise ValueError("destination must not traverse a symbolic-link or junction ancestor")

    expects_directory = expects_directory or extension.lower() == ".app"
    if requested.exists() and expects_directory and not requested.is_dir():
        raise ValueError("existing application destination must be a directory")
    if requested.exists() and not expects_directory and not requested.is_file():
        raise ValueError("existing archive destination must be a regular file")

    resolved = requested.expanduser().resolve(strict=False)
    repository = repository_root.resolve(strict=True)
    publish = publish_root.resolve(strict=True) if publish_root is not None else None
    staging = [root.resolve(strict=True) for root in staging_roots]
    home = pathlib.Path.home().resolve(strict=True)
    anchor = pathlib.Path(resolved.anchor)
    protected_roots = {anchor, home, repository}
    if publish is not None:
        protected_roots.add(publish)
    if resolved in protected_roots:
        raise ValueError("destination is a protected root")
    if publish is not None and is_within(resolved, publish):
        raise ValueError("destination must remain outside the publish tree")
    if any(is_within(resolved, root) for root in staging):
        raise ValueError("destination must remain outside package staging trees")
    if not allow_repository_target and is_within(resolved, repository):
        raise ValueError("caller-supplied destination must remain outside the repository")
    return resolved


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--target", required=True, type=pathlib.Path)
    target_kind = parser.add_mutually_exclusive_group(required=True)
    target_kind.add_argument("--extension")
    target_kind.add_argument("--directory", action="store_true")
    parser.add_argument("--repository-root", required=True, type=pathlib.Path)
    parser.add_argument("--publish-root", type=pathlib.Path)
    parser.add_argument("--staging-root", action="append", default=[], type=pathlib.Path)
    parser.add_argument("--allow-repository-target", action="store_true")
    args = parser.parse_args()
    try:
        resolved = validate_target(
            args.target,
            None if args.directory else args.extension,
            args.repository_root,
            args.publish_root,
            args.allow_repository_target,
            args.staging_root,
        )
    except (OSError, ValueError) as error:
        parser.error(str(error))
    print(resolved)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
