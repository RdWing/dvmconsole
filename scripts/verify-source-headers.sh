#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
COPYRIGHT_MARKER="SPDX-FileCopyrightText: 2025-2026 RdWing"
LICENSE_MARKER="SPDX-License-Identifier: AGPL-3.0-only"
missing=0

while IFS= read -r -d '' file; do
    [[ -f "$ROOT_DIR/$file" ]] || continue
    header="$(sed -n '1,8p' "$ROOT_DIR/$file")"
    if [[ "$header" != *"$COPYRIGHT_MARKER"* || "$header" != *"$LICENSE_MARKER"* ]]; then
        printf 'Missing required source header: %s\n' "$file" >&2
        missing=1
    fi
done < <(
    git -C "$ROOT_DIR" ls-files -z \
        'src/**/*.cs' \
        'src/**/*.axaml' \
        'native/**/*.c' \
        'native/**/*.h' \
        'native/**/*.m' \
        'native/**/*.rs' \
        'scripts/*.py' \
        'scripts/tests/*.py' \
        'scripts/*.sh' \
        'scripts/*.ps1' \
        'packaging/linux/AppRun' \
        'packaging/linux/Dockerfile'
)

if ((missing != 0)); then
    exit 1
fi

printf 'First-party source headers verified.\n'
