#!/usr/bin/env python3
"""Exercise publication rejection using complete synthetic qualification bundles."""

import datetime
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import release_candidate as candidate

SPEC = importlib.util.spec_from_file_location("release_evidence", Path(__file__).with_name("verify-release-evidence.py"))
gate = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(gate)


class ReleaseEvidenceTests(unittest.TestCase):
    REVISION = "a" * 40

    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix="zklink-release-gate-unit-")
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name).resolve()
        patcher = patch.object(candidate, "ROOT", self.root)
        patcher.start()
        self.addCleanup(patcher.stop)
        patcher = patch.object(candidate.subprocess, "check_output", return_value=self.REVISION + "\n")
        patcher.start()
        self.addCleanup(patcher.stop)
        for name in ["Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.Config", "src/ZkLink/Client.cs"]:
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text("recorded compiler input\n")
        self.feed = self.root / "feed"
        self.feed.mkdir()
        for suffix in [".nupkg", ".snupkg"]:
            (self.feed / ("ZkLink.1.0.0" + suffix)).write_bytes(b"synthetic identity fixture" + suffix.encode())
        self.candidate = candidate.write_candidate(self.feed, "1.0.0", self.REVISION)
        self.candidate_hash = candidate.sha256(self.feed / "candidate.json")
        self.bundle = self.root / "evidence"
        self.bundle.mkdir()
        self.proof("review.txt", "Synthetic review proof for gate unit tests only.\n")
        self.proof("runtime-inputs.json", json.dumps({"sourceSha256": candidate.runtime_inputs()}))

        def checked(**fields):
            return {"passed": True, "evidence": ["review.txt"], "input_manifest": "runtime-inputs.json", **fields}

        self.record = {"schema_version": 1, "candidate_run_id": 123, "candidate_manifest_sha256": self.candidate_hash,
                       "runtime_inputs": self.candidate["runtime_inputs"], "unresolved_failures": [],
                       "managed": [checked(system=system, candidate_manifest_sha256=self.candidate_hash) for system in gate.MANAGED_SYSTEMS],
                       "native": [checked(rid=rid, candidate_manifest_sha256=self.candidate_hash,
                                          rooted_analysis=True, unrooted_live=True, online_revocation=True,
                                          sent_unknown=True, server_versions=sorted(gate.SERVER_VERSIONS)) for rid in gate.NATIVE_TARGETS],
                       "required_cases": {name: checked() for name in gate.REQUIRED_CASES},
                       "failure_reviews": {name: checked(resolution="Synthetic resolved failure for unit test.") for name in gate.FAILURE_REVIEWS},
                       "soak": [], "benchmarks": [checked(suite=suite, controlled_runner=True, repetitions=2,
                                                            runner_settings="review.txt", raw_results="review.txt", baseline_diff="review.txt")
                                                        for suite in gate.BENCHMARK_SUITES]}
        for version in gate.SERVER_VERSIONS:
            start = datetime.datetime(2026, 10, 31, tzinfo=datetime.timezone.utc)
            rows = []
            for index in range(721):
                elapsed = index * gate.MAXIMUM_SAMPLE_GAP_MILLISECONDS
                rows.append({"kind": "baseline" if index == 0 else "summary" if index == 720 else "checkpoint",
                             "reported_at_utc": (start + datetime.timedelta(milliseconds=elapsed)).isoformat(),
                             "elapsed_ms": elapsed, "duration_seconds": gate.SOAK_SECONDS, "seed": 1729,
                             "rounds": index + 5, "crud_started": (index + 5) * 2, "crud_succeeded": (index + 5) * 2,
                             "canceled_not_sent": index + 5, "canceled_unknown": index + 5, "reconnects": index + 5,
                             "owned_counters": 0, "owned_live_tcp": 0, "owned_listeners": 0,
                             "retained_heap_bytes": 1000, "heap_growth_limit_bytes": 16 * 1024 * 1024,
                             "threads": 1, "thread_growth_limit": 32})
            name = version + ".jsonl"
            self.proof(name, "\n".join(map(json.dumps, rows)) + "\n")
            self.record["soak"].append(checked(version=version, cleanup_verified=True, series=name))

    def proof(self, name, text):
        (self.bundle / name).write_text(text)

    def verify(self):
        self.record["files"] = {path.name: gate.digest(path) for path in self.bundle.iterdir() if path.name != "release-evidence.json"}
        (self.bundle / "release-evidence.json").write_text(json.dumps(self.record))
        return gate.verify(self.bundle, self.feed, self.candidate_hash)

    def test_complete_synthetic_bundle_exercises_the_acceptance_path(self):
        self.assertEqual(self.verify()["candidate_manifest_sha256"], self.candidate_hash)

    def test_missing_platform_and_unverified_native_behavior_are_rejected(self):
        self.record["native"].pop()
        with self.assertRaisesRegex(ValueError, "target matrix"):
            self.verify()

    def test_publish_only_native_evidence_does_not_qualify_live_behavior(self):
        self.record["native"][0]["unrooted_live"] = False
        with self.assertRaisesRegex(ValueError, "native behavior"):
            self.verify()

    def test_changed_consumer_candidate_is_rejected(self):
        self.record["managed"][0]["candidate_manifest_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "different candidate"):
            self.verify()

    def test_unresolved_failure_and_uncontrolled_benchmarks_are_rejected(self):
        self.record["unresolved_failures"] = ["retained failure"]
        with self.assertRaisesRegex(ValueError, "Unresolved release failures"):
            self.verify()
        self.record["unresolved_failures"] = []
        self.record["benchmarks"][0]["controlled_runner"] = False
        with self.assertRaisesRegex(ValueError, "controlled repeated"):
            self.verify()

    def test_old_measurements_do_not_qualify_changed_runtime_inputs(self):
        self.proof("runtime-inputs.json", json.dumps({"sourceSha256": {}}))
        with self.assertRaisesRegex(ValueError, "Measured runtime inputs"):
            self.verify()

    def test_short_or_gapped_soak_cannot_be_relabelled_complete(self):
        name = self.record["soak"][0]["series"]
        rows = [json.loads(line) for line in (self.bundle / name).read_text().splitlines()]
        original = list(rows)
        rows[-1]["duration_seconds"] = 120
        self.proof(name, "\n".join(map(json.dumps, rows)) + "\n")
        with self.assertRaisesRegex(ValueError, "Short soak"):
            self.verify()
        original[-1]["duration_seconds"] = gate.SOAK_SECONDS
        self.proof(name, "\n".join(map(json.dumps, [original[0], original[-1]])) + "\n")
        with self.assertRaisesRegex(ValueError, "unobserved interval"):
            self.verify()

    def test_failure_record_cannot_be_hidden_between_successful_samples(self):
        name = self.record["soak"][0]["series"]
        rows = (self.bundle / name).read_text().splitlines()
        rows.insert(1, json.dumps({"kind": "failure"}))
        self.proof(name, "\n".join(rows) + "\n")
        with self.assertRaisesRegex(ValueError, "Failed soak record"):
            self.verify()

    def test_wall_clock_gap_is_rejected_even_when_elapsed_samples_are_contiguous(self):
        name = self.record["soak"][0]["series"]
        rows = [json.loads(line) for line in (self.bundle / name).read_text().splitlines()]
        unobserved_time = datetime.timedelta(hours=1)
        for row in rows[1:]:
            reported = datetime.datetime.fromisoformat(row["reported_at_utc"])
            row["reported_at_utc"] = (reported + unobserved_time).isoformat()
        self.proof(name, "\n".join(map(json.dumps, rows)) + "\n")
        with self.assertRaisesRegex(ValueError, "reporting times have an unobserved interval"):
            self.verify()

    def test_retained_resource_is_rejected(self):
        name = self.record["soak"][0]["series"]
        rows = [json.loads(line) for line in (self.bundle / name).read_text().splitlines()]
        rows[1]["owned_live_tcp"] = 1
        self.proof(name, "\n".join(map(json.dumps, rows)) + "\n")
        with self.assertRaisesRegex(ValueError, "retained owned resources"):
            self.verify()

    def test_evidence_cannot_raise_its_own_resource_growth_allowances(self):
        name = self.record["soak"][0]["series"]
        original = [json.loads(line) for line in (self.bundle / name).read_text().splitlines()]
        excessive_heap_allowance = 1024 * 1024 * 1024
        excessive_thread_allowance = 64
        for field, allowance in [("heap_growth_limit_bytes", excessive_heap_allowance),
                                 ("thread_growth_limit", excessive_thread_allowance)]:
            with self.subTest(field=field):
                rows = [dict(row, **{field: allowance}) for row in original]
                self.proof(name, "\n".join(map(json.dumps, rows)) + "\n")
                with self.assertRaisesRegex(ValueError, "resource growth allowance"):
                    self.verify()


if __name__ == "__main__":
    unittest.main()
