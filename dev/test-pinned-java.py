"""Failure and integrity checks for the shared CI JDK downloader."""

import hashlib
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ACTION = Path(__file__).resolve().parents[1] / ".github/actions/setup-pinned-java"
sys.dont_write_bytecode = True
SPEC = importlib.util.spec_from_file_location("pinned_java", ACTION / "download.py")
java = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(java)


class PinnedJavaTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.archive = Path(self.directory.name) / "jdk.tar.gz"
        self.payload = b"exact pinned JDK archive"
        self.pin = {
            "bytes": len(self.payload),
            "sha256": hashlib.sha256(self.payload).hexdigest(),
            "api_url": "https://api.example.test/asset/1",
            "download_url": "https://example.test/release/jdk.tar.gz",
        }
        self.calls = []
        self.delays = []

    def acquire(self, outcomes):
        def run(command, **kwargs):
            self.calls.append((command, kwargs))
            partial = Path(command[command.index("--output") + 1])
            outcome = outcomes[len(self.calls) - 1]
            if isinstance(outcome, Exception):
                partial.write_bytes(b"incomplete download")
                raise outcome
            partial.write_bytes(outcome)

        java.ensure_archive(self.archive, self.pin, run=run, sleep=self.delays.append)

    def assert_no_partial(self):
        self.assertFalse(self.archive.with_name(self.archive.name + ".part").exists())

    def test_verified_cache_needs_no_network(self):
        self.archive.write_bytes(self.payload)
        self.acquire([])
        self.assertEqual([], self.calls)

    def test_same_size_corrupt_cache_is_replaced(self):
        self.archive.write_bytes(b"x" * len(self.payload))
        self.acquire([self.payload])
        self.assertEqual(self.payload, self.archive.read_bytes())
        self.assert_no_partial()

    def test_transient_http_failure_retries_browser_route(self):
        self.acquire([subprocess.CalledProcessError(22, "curl"), self.payload])
        self.assertEqual([2], self.delays)
        self.assertEqual([self.pin["api_url"], self.pin["download_url"]],
                         [command[-1] for command, _ in self.calls])
        self.assertTrue(java.verified(self.archive, self.pin))
        self.assert_no_partial()

    def test_transfer_and_process_timeouts_are_retried(self):
        self.acquire([subprocess.CalledProcessError(28, "curl"),
                      subprocess.TimeoutExpired("curl", 95), self.payload])
        self.assertEqual([2, 4], self.delays)
        for command, kwargs in self.calls:
            self.assertEqual("10", command[command.index("--connect-timeout") + 1])
            self.assertEqual("90", command[command.index("--max-time") + 1])
            self.assertEqual(str(self.pin["bytes"]),
                             command[command.index("--max-filesize") + 1])
            self.assertEqual({"check": True, "timeout": 95}, kwargs)

    def test_truncated_and_same_size_wrong_hash_downloads_are_retried(self):
        self.acquire([self.payload[:-1], b"x" * len(self.payload), self.payload])
        self.assertEqual(3, len(self.calls))
        self.assertEqual(self.payload, self.archive.read_bytes())
        self.assert_no_partial()

    def test_persistent_failure_is_bounded_and_leaves_no_cache_file(self):
        self.archive.write_bytes(b"bad existing cache")
        with self.assertRaisesRegex(RuntimeError, "after 3 attempts"):
            self.acquire([subprocess.CalledProcessError(22, "curl")] * 3)
        self.assertEqual(3, len(self.calls))
        self.assertEqual([2, 4], self.delays)
        self.assertFalse(self.archive.exists())
        self.assert_no_partial()

    def test_persistent_checksum_failure_leaves_no_cache_file(self):
        with self.assertRaises(RuntimeError):
            self.acquire([b"bad bytes"] * 3)
        self.assertFalse(self.archive.exists())
        self.assert_no_partial()

    def test_pins_cover_only_explicit_host_architecture_pairs(self):
        for system, arch, rid in [("Linux", "X64", "linux-x64"),
                                  ("Linux", "ARM64", "linux-arm64"),
                                  ("Windows", "X64", "win-x64"),
                                  ("macOS", "ARM64", "osx-arm64")]:
            with self.subTest(rid=rid):
                version, actual_rid, pin = java.select_pin(system, arch)
                self.assertEqual("25.0.5+8.0.LTS", version)
                self.assertEqual(rid, actual_rid)
                self.assertRegex(pin["sha256"], r"^[a-f0-9]{64}$")
                self.assertGreater(pin["bytes"], 100_000_000)
        for system, arch in [("Windows", "ARM64"), ("macOS", "X64"), ("Other", "X64")]:
            with self.subTest(system=system, arch=arch), self.assertRaises(ValueError):
                java.select_pin(system, arch)

    def test_gradle_pin_matches_wrapper_and_rejects_checksum_drift(self):
        root = Path(self.directory.name)
        properties = root / "tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.properties"
        properties.parent.mkdir(parents=True)
        original = (ACTION.parents[2] / properties.relative_to(root)).read_text()
        properties.write_text(original)
        pin = java.select_gradle_pin(root)
        self.assertEqual("gradle-9.8.2-bin.zip", pin["name"])
        properties.write_text(original.replace(pin["sha256"], "0" * 64))
        with self.assertRaisesRegex(ValueError, "does not match"):
            java.select_gradle_pin(root)

    def test_gradle_seed_is_verified_and_does_not_mark_wrapper_installed(self):
        pin = dict(self.pin, wrapper_url="https://services.gradle.org/distributions/gradle-9.8.2-bin.zip")
        home = Path(self.directory.name) / "gradle-home"
        self.archive.write_bytes(self.payload)
        java.seed_gradle_archive(self.archive, pin, home)
        destination = java.wrapper_archive_path(pin["wrapper_url"], home)
        # Matches the path produced by the actual pinned Gradle wrapper.
        self.assertEqual(home / "wrapper/dists/gradle-9.8.2-bin/8wepis63pzuhrlfaiy04bagwu/gradle-9.8.2-bin.zip",
                         destination)
        self.assertEqual(self.payload, destination.read_bytes())
        self.assertFalse(destination.with_name(destination.name + ".ok").exists())
        self.assertFalse(destination.with_name(destination.name + ".seed-part").exists())
        self.archive.write_bytes(b"x" * len(self.payload))
        with self.assertRaisesRegex(ValueError, "unverified"):
            java.seed_gradle_archive(self.archive, pin, home)


if __name__ == "__main__":
    unittest.main()
