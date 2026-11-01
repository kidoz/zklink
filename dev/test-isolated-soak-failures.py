#!/usr/bin/env python3
"""Bounded failure probes against fresh disposable isolated soak fixtures."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import tempfile
import time

from isolated_soak import ROOT, VERSIONS, command, inspect_container, utc, validate_runtime, write_json

STARTUP_TIMEOUT = 240
CLEANUP_TIMEOUT = 240
PROBE_DURATION = 30
POLL_SECONDS = 0.2

spec = importlib.util.spec_from_file_location("soak_supervisor", Path(__file__).with_name("supervise-zookeeper-soak.py"))
supervisor = importlib.util.module_from_spec(spec)
spec.loader.exec_module(supervisor)


def probe(parent, version, fault):
    directory = parent / (version + "-" + fault)
    directory.mkdir()
    environment = os.environ.copy()
    environment.update(ZKLINK_MATRIX_ARTIFACT_ROOT=str(directory), PYTHONDONTWRITEBYTECODE="1")
    with (directory / "probe.log").open("w") as output:
        process = subprocess.Popen([sys.executable, str(ROOT / "dev/supervise-zookeeper-soak.py"),
                                    "--seconds", str(PROBE_DURATION), "--server-version", version],
                                   cwd=ROOT, env=environment, stdout=output, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + STARTUP_TIMEOUT
            manifest = None
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    raise RuntimeError("Probe runner exited before injection")
                for path in directory.glob("zklink-soak-supervisor.*/workload/zklink-soak-faults.*/*-driver.json"):
                    candidate = json.loads(path.read_text())
                    journal = path.with_name(version + "-soak.jsonl")
                    if candidate["state"] == "running" and journal.exists() and '"kind":"baseline"' in journal.read_text().replace(" ", ""):
                        manifest = candidate
                        break
                if manifest is not None:
                    break
                time.sleep(POLL_SECONDS)
            if manifest is None:
                raise RuntimeError("No active baseline before probe deadline")
            validate_runtime(inspect_container(manifest["containerId"]), manifest)
            write_json(directory / "injection.json", {"fault": fault, "serverVersion": version,
                                                      "containerId": manifest["containerId"], "injectedAtUtc": utc()})
            if fault == "driver-exit":
                command("kill", "--signal", "KILL", manifest["containerId"])
            else:
                process.send_signal(signal.SIGTERM)
            code = process.wait(timeout=CLEANUP_TIMEOUT)
            roots = list(directory.glob("zklink-soak-supervisor.*"))
            if len(roots) != 1:
                raise RuntimeError("Missing unique supervisor receipt")
            status = json.loads((roots[0] / "status.json").read_text())
            if code == 0 or status["state"] != "failed" or status.get("cleanupErrors") or status["releaseQualified"]:
                raise RuntimeError("Failed workload or cleanup incorrectly reported")
            rollback = Path(manifest["evidenceDirectory"]) / (version + "-rollback.log")
            if not rollback.exists() or any("soak-cleanup-authenticated-ready: " + service not in rollback.read_text()
                                            for service in ("zoo1", "zoo2", "zoo3")):
                raise RuntimeError("Missing fresh authenticated rollback")
            if any(supervisor.remaining_resources(manifest).values()):
                raise RuntimeError("Owned resources survived failure probe")
            write_json(directory / "review.json", {"passed": True, "fault": fault, "serverVersion": version,
                                                   "failedRunRetained": True, "authenticatedRollback": True,
                                                   "cleanupVerified": True, "releaseQualified": False, "reviewedAtUtc": utc()})
        finally:
            if process.poll() is None:
                process.send_signal(signal.SIGTERM)
                process.wait(timeout=CLEANUP_TIMEOUT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--server-version", choices=VERSIONS)
    args = parser.parse_args()
    artifact_root = Path(os.environ.get("ZKLINK_MATRIX_ARTIFACT_ROOT", tempfile.gettempdir())).resolve()
    artifact_root.mkdir(parents=True, exist_ok=True)
    parent = Path(tempfile.mkdtemp(prefix="zklink-soak-failure-probes.", dir=artifact_root))
    print("Failure probe artifacts: " + str(parent), flush=True)
    versions = (args.server_version,) if args.server_version else VERSIONS
    for version in versions:
        for fault in ("driver-exit", "supervisor-interrupt"):
            probe(parent, version, fault)
            print(version + " " + fault + ": PASS", flush=True)


if __name__ == "__main__":
    main()
