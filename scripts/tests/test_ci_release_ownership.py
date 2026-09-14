# SPDX-FileCopyrightText: 2025-2026 RdWing
# SPDX-License-Identifier: AGPL-3.0-only

from pathlib import Path
import os
import subprocess
import tempfile
import textwrap
import unittest

ROOT = Path(__file__).resolve().parents[2]


def step_script(workflow, start, end):
    section = (ROOT / workflow).read_text().split(start, 1)[1].split(end, 1)[0]
    return textwrap.dedent(section.split('run: |\n', 1)[1]).strip()


@unittest.skipUnless(os.name == "posix", "Release ownership jobs run on Ubuntu")
class ReleaseOwnershipTests(unittest.TestCase):
    def execute(self, script, command, response, **variables):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            stub = folder / command
            stub.write_text('#!/bin/sh\ncase "$*" in *compare/*) printf "%s\\n" "$STUB_MERGE_BASE";; *) printf "%s\\n" "$STUB_RESPONSE";; esac\n')
            stub.chmod(0o755)
            output = folder / 'outputs'
            environment = dict(os.environ, PATH=str(folder) + os.pathsep + os.environ['PATH'],
                               STUB_RESPONSE=response, GITHUB_OUTPUT=str(output), **variables)
            subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', script], cwd=ROOT,
                           env=environment, check=True, capture_output=True, text=True)
            return output.read_text().strip()

    def test_only_paired_neo_push_defers_to_release_tag(self):
        script = step_script('.github/workflows/build.yml', '- name: Choose build owner',
                             '- name: Verify first-party source headers')
        for event, ref, tag, expected in [
            ('push', 'refs/heads/neo', 'abc\trefs/tags/v0.8.0^{}', 'false'),
            ('push', 'refs/heads/neo', '', 'true'),
            ('push', 'refs/heads/neo', 'other\trefs/tags/v0.8.0^{}', 'true'),
            ('push', 'refs/tags/v0.8.0', 'abc\trefs/tags/v0.8.0^{}', 'true'),
            ('pull_request', 'refs/pull/1/merge', 'abc\trefs/tags/v0.8.0^{}', 'true'),
        ]:
            with self.subTest(event=event, ref=ref, tag=tag):
                self.assertEqual('build=' + expected, self.execute(script, 'git', tag,
                    GITHUB_EVENT_NAME=event, GITHUB_REF=ref, GITHUB_SHA='abc'))

    def test_testflight_requires_one_successful_qualification(self):
        script = step_script('.github/workflows/testflight.yml',
                             '- name: Verify upstream delivery qualification', '  upload:')
        for conclusion in ('success', 'skipped', 'failure', '', 'success\nsuccess'):
            with self.subTest(conclusion=conclusion):
                expected = 'true' if conclusion == 'success' else 'false'
                self.assertEqual('ready=' + expected, self.execute(script, 'gh', conclusion,
                    GH_REPO='example/console', UPSTREAM_RUN='123', UPSTREAM_SHA='abc', STUB_MERGE_BASE='abc'))

    def test_testflight_rejects_a_commit_outside_neo(self):
        script = step_script('.github/workflows/testflight.yml',
                             '- name: Verify upstream delivery qualification', '  upload:')
        with self.assertRaises(subprocess.CalledProcessError):
            self.execute(script, 'gh', 'success', GH_REPO='example/console',
                         UPSTREAM_RUN='123', UPSTREAM_SHA='untrusted', STUB_MERGE_BASE='abc')

    def test_qualification_requires_all_validation_and_tag_publication(self):
        workflow = (ROOT / '.github/workflows/build.yml').read_text()
        qualification = workflow.split('  delivery-qualification:', 1)[1]
        for job in ('test-and-publish', 'ios-checks', 'dependency-advisories', 'notarize-macos'):
            self.assertIn(f"needs.{job}.result == 'success'", qualification)
        self.assertIn("needs.publish-release.result == 'success'", qualification)
        self.assertIn("github.ref == 'refs/heads/neo' && needs.publish-release.result == 'skipped'", qualification)
