#!/usr/bin/env python3
"""Fail closed on incomplete or mismatched production qualification evidence."""

import argparse
import datetime
import hashlib
import json
from pathlib import Path

from release_candidate import read_candidate

SERVER_VERSIONS = {"3.8.7", "3.9.6"}
MANAGED_SYSTEMS = {"Linux", "Windows", "Darwin"}
NATIVE_TARGETS = {"linux-x64", "linux-arm64", "win-x64", "osx-arm64"}
BENCHMARK_SUITES = {"protocol", "live_operations", "watch_fanout", "watch_recovery"}
REQUIRED_CASES = {"authentication", "expiry", "watch_recovery", "observer_endpoints",
                  "native_sent_unknown", "native_online_revocation"}
# Keep the historical evidence key stable: source review identifies sync_timeout
# as DeleteAsync using the shared SyncCoreAsync reply helper. It remains required.
FAILURE_REVIEWS = {"sync_timeout", "proxy_frame_bound", "extra_connection_attempt",
                   "tls_rejection_timeout", "local_revocation_fetch", "watch_pressure_counter",
                   "soak_cleanup_not_empty"}
MINIMUM_SOAK_SECONDS = 6 * 60 * 60
MAXIMUM_SOAK_SECONDS = 24 * 60 * 60
RETAINED_HEAP_ALLOWANCE_BYTES = 16 * 1024 * 1024
THREAD_ALLOWANCE = 32
MILLISECONDS_PER_SECOND = 1000
MAXIMUM_SAMPLE_GAP_MILLISECONDS = 120_000
MAXIMUM_SAMPLES = 1444
MAXIMUM_EVIDENCE_FILE_BYTES = 64 * 1024 * 1024
HASH_BUFFER_BYTES = 1024 * 1024


def require(condition, message):
    if not condition:
        raise ValueError(message)


def digest(path):
    result = hashlib.sha256()
    with path.open("rb") as stream:
        while block := stream.read(HASH_BUFFER_BYTES):
            result.update(block)
    return result.hexdigest().upper()


def verify(directory, candidate_directory, candidate_sha256):
    directory = Path(directory).resolve()
    _, candidate = read_candidate(candidate_directory, candidate_sha256)
    record = json.loads((directory / "release-evidence.json").read_text(encoding="utf-8"))
    require(record.get("schema_version") == 1, "Unsupported release evidence")
    require(str(record.get("candidate_run_id", "")).isdecimal()
            and int(record["candidate_run_id"]) > 0, "Missing completed candidate run identity")
    require(record.get("candidate_manifest_sha256") == candidate_sha256.upper(), "Candidate evidence identity mismatch")
    require(record.get("runtime_inputs") == candidate["runtime_inputs"], "Qualification runtime inputs mismatch")
    require(record.get("unresolved_failures") == [], "Unresolved release failures")
    files = record.get("files", {})
    require(isinstance(files, dict) and files, "Missing retained evidence files")

    def evidence(name):
        require(isinstance(name, str) and name in files, "Missing evidence reference")
        path = directory / name
        require(not path.is_symlink() and path.resolve().is_relative_to(directory)
                and path.is_file(), "Evidence path escapes its bundle")
        require(path.stat().st_size <= MAXIMUM_EVIDENCE_FILE_BYTES, "Evidence file exceeds its bound")
        require(digest(path) == files[name], "Retained evidence checksum mismatch")
        return path

    for name in files:
        evidence(name)

    def matching_runtime(entry):
        manifest = json.loads(evidence(entry.get("input_manifest")).read_text(encoding="utf-8"))
        recorded = manifest.get("sourceSha256", {})
        require(all(isinstance(recorded.get(name), str) and recorded[name].upper() == expected
                    for name, expected in candidate["runtime_inputs"].items()),
                "Measured runtime inputs differ from the candidate")

    def checked(entry):
        require(entry.get("passed") is True, "Required verification did not pass")
        proofs = entry.get("evidence", [])
        require(isinstance(proofs, list) and proofs, "Missing verification proof")
        for name in proofs:
            evidence(name)

    for name, expected, key in [("managed", MANAGED_SYSTEMS, "system"),
                                ("native", NATIVE_TARGETS, "rid")]:
        entries = record.get(name, [])
        require(len(entries) == len(expected) and {entry.get(key) for entry in entries} == expected,
                "Incomplete " + name + " target matrix")
        for entry in entries:
            checked(entry)
            require(entry.get("candidate_manifest_sha256") == candidate_sha256.upper(), "Consumer tested a different candidate")
            if name == "native":
                require(all(entry.get(scope) is True for scope in ["rooted_analysis", "unrooted_live", "online_revocation", "sent_unknown"]),
                        "Incomplete native behavior qualification")
                require(set(entry.get("server_versions", [])) == SERVER_VERSIONS, "Incomplete native server matrix")
    cases = record.get("required_cases", {})
    require(set(cases) == REQUIRED_CASES, "Required case review is incomplete")
    for entry in cases.values():
        checked(entry)
    reviews = record.get("failure_reviews", {})
    require(set(reviews) == FAILURE_REVIEWS, "Historical failure review is incomplete")
    for entry in reviews.values():
        checked(entry)
        require(isinstance(entry.get("resolution"), str) and entry["resolution"].strip(), "Missing failure resolution")

    soaks = record.get("soak", [])
    require(len(soaks) == len(SERVER_VERSIONS)
            and {entry.get("version") for entry in soaks} == SERVER_VERSIONS, "Incomplete soak version matrix")
    for entry in soaks:
        checked(entry)
        matching_runtime(entry)
        require(entry.get("cleanup_verified") is True, "Soak cleanup is unverified")
        rows = []
        with evidence(entry.get("series")).open(encoding="utf-8") as source:
            for line in source:
                rows.append(json.loads(line))
                require(len(rows) <= MAXIMUM_SAMPLES, "Resource series exceeds its declared sample bound")
        require(len(rows) >= 2 and rows[0].get("kind") == "baseline"
                and rows[-1].get("kind") == "summary", "Incomplete soak resource series")
        baseline, summary = rows[0], rows[-1]
        duration = summary.get("duration_seconds")
        require(type(duration) is int and MINIMUM_SOAK_SECONDS <= duration <= MAXIMUM_SOAK_SECONDS
                and summary.get("elapsed_ms", 0) >= duration * MILLISECONDS_PER_SECOND,
                "Short or unsupported soak duration")
        require(summary.get("crud_started", 0) == summary.get("crud_succeeded", -1)
                and summary.get("crud_succeeded", 0) > 0, "Incomplete soak operations")
        require(all(summary.get(name) == summary.get("rounds")
                    for name in ["canceled_not_sent", "canceled_unknown", "reconnects"]), "Incomplete soak recovery")
        previous_elapsed = None
        previous_utc = None
        for row in rows:
            require(row.get("kind") in {"baseline", "checkpoint", "summary"}, "Failed soak record")
            require(row.get("seed") == summary.get("seed") and row.get("duration_seconds") == duration,
                    "Mixed resource series inputs")
            require(all(row.get(name) == 0 for name in ["owned_counters", "owned_live_tcp", "owned_listeners"]),
                    "Soak retained owned resources")
            require(row.get("heap_growth_limit_bytes") == RETAINED_HEAP_ALLOWANCE_BYTES
                    and row.get("thread_growth_limit") == THREAD_ALLOWANCE,
                    "Soak resource growth allowance differs from the release bound")
            require(row["retained_heap_bytes"] <= baseline["retained_heap_bytes"] + RETAINED_HEAP_ALLOWANCE_BYTES
                    and row["threads"] <= baseline["threads"] + THREAD_ALLOWANCE, "Soak resource growth exceeded its bound")
            elapsed = row["elapsed_ms"]
            utc = datetime.datetime.fromisoformat(row["reported_at_utc"].replace("Z", "+00:00"))
            require(utc.tzinfo is not None, "Soak reporting time has no offset")
            if previous_elapsed is not None:
                require(0 < elapsed - previous_elapsed <= MAXIMUM_SAMPLE_GAP_MILLISECONDS,
                        "Resource series has an unobserved interval")
                require(0 < (utc - previous_utc).total_seconds()
                        <= MAXIMUM_SAMPLE_GAP_MILLISECONDS / MILLISECONDS_PER_SECOND,
                        "Resource reporting times have an unobserved interval")
            previous_elapsed, previous_utc = elapsed, utc
        require((previous_utc - datetime.datetime.fromisoformat(baseline["reported_at_utc"])).total_seconds()
                >= duration - MAXIMUM_SAMPLE_GAP_MILLISECONDS / MILLISECONDS_PER_SECOND,
                "Resource reporting times do not cover the soak duration")

    benchmarks = record.get("benchmarks", [])
    require(len(benchmarks) == len(BENCHMARK_SUITES)
            and {entry.get("suite") for entry in benchmarks} == BENCHMARK_SUITES, "Incomplete benchmark report")
    for entry in benchmarks:
        checked(entry)
        matching_runtime(entry)
        require(entry.get("controlled_runner") is True and entry.get("repetitions", 0) >= 2,
                "Missing controlled repeated measurements")
        for name in ["runner_settings", "raw_results", "baseline_diff"]:
            evidence(entry.get(name))
    return record


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--evidence", required=True, type=Path)
    parser.add_argument("--candidate", required=True, type=Path)
    parser.add_argument("--candidate-sha256", required=True)
    args = parser.parse_args()
    verify(args.evidence, args.candidate, args.candidate_sha256)
    print("Production release evidence: PASS")


if __name__ == "__main__":
    main()
