#!/usr/bin/env bash
# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

# Sourced by hosted signing jobs. Preserve access to the runner's existing
# certificate chains while making the temporary signing identity discoverable.
neo_original_keychains=()
neo_keychain_search_saved=false

neo_save_keychain_search_list() {
    security list-keychains -d user > "$1/keychain-search-list.txt"
    python3 - "$1" <<'PY'
import shlex
import sys
from pathlib import Path
root = Path(sys.argv[1])
paths = shlex.split((root / 'keychain-search-list.txt').read_text())
(root / 'keychain-search-list.bin').write_bytes(
    b''.join(path.encode() + b'\0' for path in paths))
PY
    while IFS= read -r -d '' neo_existing_keychain; do
        neo_original_keychains+=("$neo_existing_keychain")
    done < "$1/keychain-search-list.bin"
    neo_keychain_search_saved=true
}

neo_add_signing_keychain() {
    if (( ${#neo_original_keychains[@]} )); then
        security list-keychains -d user -s "$1" "${neo_original_keychains[@]}"
    else
        security list-keychains -d user -s "$1"
    fi
}

neo_restore_keychain_search_list() {
    if [[ "$neo_keychain_search_saved" == true ]]; then
        if (( ${#neo_original_keychains[@]} )); then
            security list-keychains -d user -s "${neo_original_keychains[@]}"
        else
            security list-keychains -d user -s
        fi
    fi
}
