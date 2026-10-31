#!/usr/bin/env python3
"""Reject candidate substitution and stale compiler inputs before any consumer runs."""

import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import release_candidate as candidate


class CandidateIdentityTests(unittest.TestCase):
    VERSION = "1.0.0"
    REVISION = "a" * 40

    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="zklink-candidate-unit-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name).resolve()
        self.root_patch = patch.object(candidate, "ROOT", self.root)
        self.root_patch.start()
        self.addCleanup(self.root_patch.stop)
        for name in ["Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.Config", "src/ZkLink/Client.cs"]:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("recorded source\n")
        self.feed = self.root / "feed"
        self.feed.mkdir()
        for suffix in [".nupkg", ".snupkg"]:
            (self.feed / ("ZkLink." + self.VERSION + suffix)).write_bytes(b"identity fixture" + suffix.encode())
        candidate.write_candidate(self.feed, self.VERSION, self.REVISION)
        self.digest = candidate.sha256(self.feed / candidate.MANIFEST_NAME)

    def read(self):
        return candidate.read_candidate(self.feed, self.digest, self.REVISION)

    def test_exact_candidate_is_accepted_without_repacking(self):
        directory, data = self.read()
        self.assertEqual(directory, self.feed)
        self.assertEqual(data["version"], self.VERSION)
        self.assertEqual(data["revision"], self.REVISION)

    def test_package_or_symbols_substitution_is_rejected(self):
        for suffix in [".nupkg", ".snupkg"]:
            path = self.feed / ("ZkLink." + self.VERSION + suffix)
            original = path.read_bytes()
            with self.subTest(suffix=suffix):
                path.write_bytes(b"substitute")
                with self.assertRaisesRegex(ValueError, "package or symbol identity"):
                    self.read()
            path.write_bytes(original)

    def test_rewritten_manifest_does_not_redefine_the_expected_identity(self):
        (self.feed / ("ZkLink." + self.VERSION + ".nupkg")).write_bytes(b"substitute")
        candidate.write_candidate(self.feed, self.VERSION, self.REVISION)
        with self.assertRaisesRegex(ValueError, "manifest identity"):
            self.read()

    def test_different_revision_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "revision or runtime inputs"):
            candidate.read_candidate(self.feed, self.digest, "b" * 40)

    def test_changed_or_additional_runtime_source_is_rejected(self):
        for name in ["src/ZkLink/Client.cs", "src/ZkLink/Additional.cs"]:
            path = self.root / name
            original = path.read_bytes() if path.exists() else None
            with self.subTest(name=name):
                path.write_bytes(b"different source")
                with self.assertRaisesRegex(ValueError, "runtime inputs"):
                    self.read()
            if original is None:
                path.unlink()
            else:
                path.write_bytes(original)

    def test_missing_symbols_are_rejected(self):
        (self.feed / ("ZkLink." + self.VERSION + ".snupkg")).unlink()
        with self.assertRaises((ValueError, FileNotFoundError)):
            self.read()

    def test_manifest_path_traversal_is_rejected_before_file_access(self):
        path = self.feed / candidate.MANIFEST_NAME
        data = json.loads(path.read_text())
        data["files"]["../untrusted.nupkg"] = "0" * 64
        path.write_text(json.dumps(data))
        with self.assertRaisesRegex(ValueError, "exactly one core package"):
            candidate.read_candidate(self.feed, candidate.sha256(path), self.REVISION)

    def test_partial_environment_configuration_never_falls_back_to_packing(self):
        for values in [{"ZKLINK_CANDIDATE_DIRECTORY": str(self.feed)},
                       {"ZKLINK_CANDIDATE_MANIFEST_SHA256": self.digest}]:
            with self.subTest(values=list(values)), patch.dict(os.environ, values, clear=True):
                with self.assertRaisesRegex(ValueError, "Both candidate"):
                    candidate.from_environment(self.REVISION)


if __name__ == "__main__":
    unittest.main()
