# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Run shell fixtures with Git Bash on Windows and native Bash elsewhere."""

import os
from pathlib import Path
import shutil
import subprocess
import sys


def find_git_bash(git):
    # Git is exposed through cmd/bin on Windows and mingw64/bin in Git Bash.
    for parent in Path(git).resolve().parents[:3]:
        for relative in ('bash.exe', 'bin/bash.exe', 'usr/bin/bash.exe'):
            candidate = parent / relative
            if candidate.is_file():
                return str(candidate)
    return None


def bash_path():
    if os.name == 'nt':
        git = shutil.which('git')
        if git:
            candidate = find_git_bash(git)
            if candidate:
                return candidate
        raise RuntimeError('Shell tests require Git for Windows Bash, not the WSL launcher.')
    bash = shutil.which('bash')
    if not bash:
        raise RuntimeError('Shell tests require Bash.')
    return bash


def write_executable(path, body):
    path.write_text('#!/bin/bash\n' + body, encoding='utf-8', newline='\n')
    path.chmod(0o755)


def shell_environment(root, binaries, **values):
    # Use the running test interpreter instead of a Windows Store python3 alias.
    interpreter = Path(sys.executable).as_posix().replace("'", "'\"'\"'")
    write_executable(binaries / 'python3', f"exec '{interpreter}' \"$@\"\n")
    env = dict(os.environ, PATH=str(binaries) + os.pathsep + os.environ['PATH'])
    env.update({key: value.as_posix() if isinstance(value, Path) else value
                for key, value in values.items()})
    env['NEO_TEST_BIN'] = binaries.as_posix()
    env['TMPDIR'] = root.as_posix()
    return env


def run_shell(script, *, env, cwd=None):
    # MSYS prepends its own tools while Bash starts. Reapply fixture priority
    # inside that shell so real uname/git cannot shadow the test doubles.
    fixture_path = '$(cygpath -u "$NEO_TEST_BIN")' if os.name == 'nt' else '$NEO_TEST_BIN'
    command = [bash_path(), '-c',
               f'if [[ -n "$NEO_TEST_BIN" ]]; then export PATH="{fixture_path}:$PATH"; fi; '
               'script="$1"; shift; source "$script" "$@"', 'shell-fixture',
               Path(script).as_posix()]
    return subprocess.run(command, cwd=cwd, env=env, capture_output=True, text=True)
