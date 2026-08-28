#!/usr/bin/env bash
set -euo pipefail

REPOSITORY_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPOSITORY_ROOT"

# fnecore is a pinned upstream submodule compiled by DvmConsole.Fne. Its
# formatting belongs upstream; this gate covers all application-owned code.
dotnet format dvmconsole.sln \
    --no-restore \
    --verify-no-changes \
    --exclude fnecore src/DvmConsole.Fne \
    --verbosity minimal
