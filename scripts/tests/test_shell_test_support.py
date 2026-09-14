# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

"""Git Bash discovery must work from both native Windows and MSYS PATH layouts."""

from pathlib import Path
import tempfile
import unittest

from shell_test_support import find_git_bash


class GitBashDiscoveryTests(unittest.TestCase):
    def test_finds_bash_for_each_git_entry_point(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder) / 'Git installation with spaces'
            bash = root / 'bin/bash.exe'
            bash.parent.mkdir(parents=True)
            bash.touch()
            for relative in ('bin/git.exe', 'cmd/git.exe', 'mingw64/bin/git.exe'):
                with self.subTest(entry=relative):
                    git = root / relative
                    git.parent.mkdir(parents=True, exist_ok=True)
                    git.touch()
                    self.assertEqual(str(bash.resolve()), find_git_bash(git))
