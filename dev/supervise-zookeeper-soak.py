#!/usr/bin/env python3
"""Run an immutable, continuously observed isolated soak and retain failed evidence."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import signal
import subprocess
import sys
import tempfile
import time

from isolated_soak import (ROOT, VERSIONS, MINIMUM_DURATION, MAXIMUM_DURATION, MAXIMUM_SEED, command, inspect_container, member, pinned_image,
                           remove_owned, utc, validate_binaries, validate_identity, validate_runtime, write_json)

GIB = 1024 ** 3
KIB = 1024
MINIMUM_HOST_FREE = 5 * GIB
MINIMUM_VM_FREE = 2 * GIB
OBSERVATION_INTERVAL = 10
MAXIMUM_OBSERVATION_GAP = 120
CLEANUP_GRACE = 180
PROCESS_REAP_TIMEOUT = 10
TOTAL_OVERHEAD = 600
FULL_DURATION = 21600
PREFLIGHT_DURATION = 30
WARMUP_ROUNDS = 5
MINIMUM_SAMPLES = 2
ENSEMBLE_MEMBERS = 3
MILLISECONDS_PER_SECOND = 1000


def sources():
    paths = set()
    for directory in ("src/ZkLink", "tests/ZkLink.FaultTests"):
        paths.update(path for path in (ROOT / directory).rglob("*")
                     if path.is_file() and not {"bin", "obj"}.intersection(path.relative_to(ROOT).parts))
    for pattern in ("Directory.*", "NuGet.Config", "global.json", "justfile", "compose*.yaml",
                    "dev/*soak*", "dev/test-zookeeper-node-faults.sh", "dev/zookeeper-*.env",
                    "dev/zookeeper-healthcheck.sh", "tests/Shared/TelemetryCapture.cs",
                    "tests/ZkLink.IntegrationTests/LiveZooKeeperFixture.cs"):
        paths.update(path for path in ROOT.glob(pattern) if path.is_file())
    return {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in sorted(paths)}


def source_unchanged(expected):
    if sources() != expected:
        raise RuntimeError("Soak source inputs changed during execution")


def check_observation(previous_utc, previous_monotonic, current_utc, current_monotonic, free_bytes):
    utc_gap = (current_utc - previous_utc).total_seconds()
    monotonic_gap = current_monotonic - previous_monotonic
    if not 0 <= utc_gap <= MAXIMUM_OBSERVATION_GAP or not 0 <= monotonic_gap <= MAXIMUM_OBSERVATION_GAP:
        raise RuntimeError("Supervisor continuity bound exceeded")
    if free_bytes < MINIMUM_HOST_FREE:
        raise RuntimeError("Host free space below declared minimum")


def manifests(directory):
    return [json.loads(path.read_text()) for path in sorted(directory.glob("zklink-soak-faults.*/*-driver.json"))]


def fixture_manifests(directory):
    result = []
    for path in sorted(directory.glob("zklink-soak-faults.*/*-fixture.json")):
        driver = path.with_name(path.name.replace("-fixture.json", "-driver.json"))
        result.append(json.loads((driver if driver.exists() else path).read_text()))
    return result


def check_deadline(now, deadline):
    if now > deadline:
        raise RuntimeError("Owned soak exceeded total wall-time guard")


def remaining_resources(manifest):
    validate_identity(manifest)
    result = {}
    for kind, listing in (("containers", ("ps", "--all", "--quiet")),
                          ("networks", ("network", "ls", "--quiet")),
                          ("volumes", ("volume", "ls", "--quiet"))):
        result[kind] = command(*listing, "--filter", "label=com.docker.compose.project=" + manifest["project"]).splitlines()
    driver = inspect_container(manifest.get("containerId") or manifest["containerName"], missing=True)
    result["driver"] = [] if driver is None else [driver["Id"]]
    return result


def observe_runtime(manifest):
    validate_identity(manifest)
    if manifest["state"] not in ("created", "running"):
        return {"state": manifest["state"]}
    info = inspect_container(manifest["containerId"])
    validate_runtime(info, manifest)
    validate_binaries(manifest)
    if info["State"].get("OOMKilled") or (info["State"]["Status"] == "exited" and info["State"]["ExitCode"] != 0):
        raise RuntimeError("Observed runtime failure")
    if info["State"]["Status"] not in ("created", "running", "exited"):
        raise RuntimeError("Unexpected runtime lifecycle state")
    for identity in (manifest["namespaceContainerId"], manifest["maintenanceContainerId"]):
        member(identity, manifest["project"], manifest["runId"])
    row = {"state": info["State"]["Status"], "containerId": info["Id"], "platform": manifest["platform"]}
    if info["State"]["Running"]:
        row["stats"] = command("stats", "--no-stream", "--format", "{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}|{{.PIDs}}", info["Id"])
    return row


def observe_storage(manifest):
    # Only sample members authenticated by the nonce/project labels, never arbitrary containers.
    ids = command("ps", "--quiet", "--filter", "label=com.docker.compose.project=" + manifest["project"]).splitlines()
    if not ids:
        return {"state": "removed"}
    if len(ids) != ENSEMBLE_MEMBERS:
        raise RuntimeError("Unexpected ensemble member count")
    for identity in ids:
        member(identity, manifest["project"], manifest["runId"])
    row = {"containers": command("stats", "--no-stream", "--format", "{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}|{{.BlockIO}}|{{.PIDs}}", *ids).splitlines()}
    counters = command("exec", ids[0], "cat", "/proc/pressure/io", "/proc/diskstats", "/proc/meminfo")
    row["vmIoPressure"] = [line for line in counters.splitlines() if line.startswith(("some ", "full "))]
    row["vmMemoryKiB"], row["vmDisks"] = {}, []
    for line in counters.splitlines():
        fields = line.split()
        if len(fields) >= 14 and re.fullmatch(r"(?:[sv]d[a-z]+|nvme\d+n\d+)", fields[2]):
            row["vmDisks"].append({"device": fields[2], "reads": int(fields[3]), "readMilliseconds": int(fields[6]),
                                   "writes": int(fields[7]), "writeMilliseconds": int(fields[10]),
                                   "ioInProgress": int(fields[11]), "ioMilliseconds": int(fields[12]),
                                   "weightedIoMilliseconds": int(fields[13])})
        if fields and fields[0].rstrip(":") in ("MemTotal", "MemAvailable", "Dirty", "Writeback", "SwapFree"):
            row["vmMemoryKiB"][fields[0].rstrip(":")] = int(fields[1])
    if not row["vmIoPressure"] or not row["vmDisks"]:
        raise RuntimeError("Required VM storage counters unavailable")
    row["vmDiskAvailableBytes"] = int(command("exec", ids[0], "df", "-Pk", "/datalog").splitlines()[-1].split()[3]) * KIB
    if row["vmDiskAvailableBytes"] < MINIMUM_VM_FREE:
        raise RuntimeError("VM disk free space below declared minimum")
    return row


def validate_samples(samples, duration, seed):
    if len(samples) < MINIMUM_SAMPLES or samples[0]["kind"] != "baseline" or samples[-1]["kind"] != "summary":
        raise RuntimeError("Missing completed soak journal")
    baseline, summary = samples[0], samples[-1]
    if summary["duration_seconds"] != duration or summary["seed"] != seed or summary["elapsed_ms"] < duration * MILLISECONDS_PER_SECOND:
        raise RuntimeError("Duration or seed evidence mismatch")
    if summary["rounds"] <= WARMUP_ROUNDS or summary["crud_started"] != summary["crud_succeeded"]:
        raise RuntimeError("Incomplete workload")
    if any(summary[name] != summary["rounds"] for name in ("canceled_not_sent", "canceled_unknown", "reconnects")):
        raise RuntimeError("Incomplete cancellation or recovery evidence")
    previous = None
    for sample in samples:
        reported = datetime.datetime.fromisoformat(sample["reported_at_utc"].replace("Z", "+00:00"))
        if reported.tzinfo is None:
            raise RuntimeError("Resource reporting time lacks an offset")
        if previous is not None:
            elapsed_gap = sample["elapsed_ms"] - previous["elapsed_ms"]
            utc_gap = (reported - datetime.datetime.fromisoformat(previous["reported_at_utc"].replace("Z", "+00:00"))).total_seconds()
            if not 0 < elapsed_gap <= MAXIMUM_OBSERVATION_GAP * MILLISECONDS_PER_SECOND or not 0 < utc_gap <= MAXIMUM_OBSERVATION_GAP:
                raise RuntimeError("Resource series has an unobserved interval")
        previous = sample
        if sample["kind"] not in ("baseline", "checkpoint", "summary"):
            raise RuntimeError("Soak failure record")
        if any(sample[name] != 0 for name in ("owned_counters", "owned_live_tcp", "owned_listeners")):
            raise RuntimeError("Retained owned resources")
        if sample["retained_heap_bytes"] > baseline["retained_heap_bytes"] + sample["heap_growth_limit_bytes"]:
            raise RuntimeError("Retained heap growth limit exceeded")
        if sample["threads"] > baseline["threads"] + sample["thread_growth_limit"]:
            raise RuntimeError("Thread growth limit exceeded")
    return summary


def audit(directory, duration, seed, versions):
    roots = list(directory.glob("zklink-soak-faults.*"))
    if len(roots) != 1:
        raise RuntimeError("Expected one owned evidence directory")
    evidence = roots[0]
    output = (directory / "runner.log").read_text()
    results = []
    for version in versions:
        summary = validate_samples([json.loads(line) for line in (evidence / (version + "-soak.jsonl")).read_text().splitlines()], duration, seed)
        manifest = json.loads((evidence / (version + "-driver.json")).read_text())
        validate_identity(manifest)
        if manifest["state"] != "passed" or not manifest.get("cleanupVerified") or manifest.get("exitCode") != 0:
            raise RuntimeError("Isolated runtime completion missing")
        if manifest["image"] != pinned_image() or not manifest["platform"].startswith("linux-"):
            raise RuntimeError("Runtime pin or platform mismatch")
        if "ZooKeeper " + version + ": PASS (seeded churn" not in output:
            raise RuntimeError("Missing authenticated fixture completion")
        roles = [(evidence / (version + "-" + node + "-role-after.txt")).read_text() for node in ("zoo1", "zoo2", "zoo3")]
        if sum("Mode: leader" in role for role in roles) != 1 or sum("Mode: follower" in role for role in roles) != 2:
            raise RuntimeError("Final server roles mismatch")
        if any(remaining_resources(manifest).values()):
            raise RuntimeError("Owned fixture resources remain")
        observed = [fixture for line in (directory.parent / "resources.jsonl").read_text().splitlines()
                    for fixture in json.loads(line)["fixtures"] if fixture["project"] == manifest["project"]
                    and fixture.get("driver", {}).get("stats") and fixture.get("storage", {}).get("vmDiskAvailableBytes")]
        if not observed:
            raise RuntimeError("Missing live driver and VM resource observations")
        results.append({"serverVersion": version, "platform": manifest["platform"], "image": manifest["image"],
                        "summary": summary, "cleanupVerified": True})
    write_json(directory / "review.json", {"passed": True, "releaseQualified": False, "versions": results, "reviewedAtUtc": utc()})
    return results


class Supervisor:
    def __init__(self, directory, args):
        self.directory, self.args, self.child = directory, args, None
        self.interrupted = False
        self.inhibitor = None
        self.status = {"state": "starting", "releaseQualified": False, "supervisorPid": os.getpid(),
                       "runDirectory": str(directory), "startedAtUtc": utc()}

    def save(self, **changes):
        self.status.update(changes, updatedAtUtc=utc())
        write_json(self.directory / "status.json", self.status)

    def interrupt(self, _number, _frame):
        self.interrupted = True

    def stop_inhibitor(self):
        if self.inhibitor is None or self.inhibitor.poll() is not None:
            return
        self.inhibitor.terminate()
        try:
            self.inhibitor.wait(timeout=PROCESS_REAP_TIMEOUT)
        except subprocess.TimeoutExpired:
            self.inhibitor.kill()
            self.inhibitor.wait(timeout=PROCESS_REAP_TIMEOUT)
            raise RuntimeError("Owned idle-sleep inhibitor exceeded cleanup grace")

    def stop(self):
        if self.child is None or self.child.poll() is not None:
            return
        try:
            os.killpg(self.child.pid, signal.SIGTERM)
        except ProcessLookupError:
            return
        try:
            self.child.wait(timeout=CLEANUP_GRACE)
        except subprocess.TimeoutExpired:
            os.killpg(self.child.pid, signal.SIGKILL)
            self.child.wait(timeout=PROCESS_REAP_TIMEOUT)
            raise RuntimeError("Owned runner exceeded cleanup grace")

    def execute(self, name, duration, expected):
        source_unchanged(expected)
        directory = self.directory / name
        directory.mkdir()
        environment = os.environ.copy()
        environment.update(ZKLINK_MATRIX_ARTIFACT_ROOT=str(directory), PYTHONDONTWRITEBYTECODE="1",
                           ZKLINK_SOAK_EXECUTION="isolated", ZKLINK_SOAK_DURATION_SECONDS=str(duration),
                           ZKLINK_SOAK_SEED=str(self.args.seed), ZKLINK_ISOLATED_SOAK_VERSIONS=" ".join(self.args.versions))
        started = time.monotonic()
        deadline = started + len(self.args.versions) * duration + TOTAL_OVERHEAD
        self.save(state=name, command=["bash", "dev/test-zookeeper-node-faults.sh", "soak"], durationSeconds=duration)
        with (directory / "runner.log").open("w") as output:
            self.child = subprocess.Popen(["bash", "dev/test-zookeeper-node-faults.sh", "soak"], cwd=ROOT,
                                          env=environment, stdout=output, stderr=subprocess.STDOUT, start_new_session=True)
            self.save(runnerPid=self.child.pid)
            previous_utc, previous_monotonic = datetime.datetime.now(datetime.timezone.utc), time.monotonic()
            while True:
                current_utc, current_monotonic = datetime.datetime.now(datetime.timezone.utc), time.monotonic()
                free = shutil.disk_usage(self.directory).free
                check_observation(previous_utc, previous_monotonic, current_utc, current_monotonic, free)
                previous_utc, previous_monotonic = current_utc, current_monotonic
                source_unchanged(expected)
                if self.inhibitor is not None and self.inhibitor.poll() is not None:
                    raise RuntimeError("Owned idle-sleep inhibitor exited")
                row = {"reportedAtUtc": utc(), "workload": name, "hostFreeBytes": free, "fixtures": []}
                for manifest in manifests(directory):
                    if manifest["state"] in ("created", "running"):
                        try:
                            row["fixtures"].append({"project": manifest["project"], "driver": observe_runtime(manifest),
                                                    "storage": observe_storage(manifest)})
                        except RuntimeError:
                            # Final removal can race a sample; only a terminal worker receipt permits that race.
                            latest = json.loads((Path(manifest["evidenceDirectory"]) / (manifest["serverVersion"] + "-driver.json")).read_text())
                            if latest["state"] in ("created", "running"):
                                raise
                            row["fixtures"].append({"project": manifest["project"], "state": latest["state"]})
                with (self.directory / "resources.jsonl").open("a") as resources:
                    resources.write(json.dumps(row) + "\n")
                self.save(hostFreeBytes=free, elapsedCommandSeconds=round(time.monotonic() - started))
                if self.interrupted:
                    raise RuntimeError("Supervisor interrupted")
                code = self.child.poll()
                if code is not None:
                    if code:
                        raise RuntimeError("Owned soak command failed with exit " + str(code))
                    break
                check_deadline(time.monotonic(), deadline)
                # Short waits keep signal handling responsive; sampling remains bounded.
                for _ in range(OBSERVATION_INTERVAL):
                    if self.interrupted or self.child.poll() is not None:
                        break
                    time.sleep(1)
        source_unchanged(expected)
        return audit(directory, duration, self.args.seed, self.args.versions)

    def cleanup(self):
        errors = []
        try:
            self.stop()
        except Exception as error:
            errors.append(type(error).__name__)
        for manifest in fixture_manifests(self.directory / self.status.get("state", "workload")):
            try:
                remove_owned(manifest)
                remaining = remaining_resources(manifest)
                if any(remaining.values()):
                    # A timed-out shell may need emergency teardown. Never claim authenticated rollback here.
                    ids = remaining["containers"]
                    for identity in ids:
                        member(identity, manifest["project"], manifest["runId"], require_running=False)
                    environment = os.environ.copy()
                    environment.update(ZKLINK_FAULT_RUN_ID=manifest["runId"], ZKLINK_FAULT_REPO_ROOT=str(ROOT))
                    with (self.directory / "emergency-cleanup.log").open("a") as output:
                        subprocess.run(["docker", "compose", "-f", "compose.yaml", "-f", "compose.fault.yaml",
                                        "--env-file", "dev/zookeeper-" + manifest["serverVersion"] + ".env",
                                        "--project-name", manifest["project"], "down", "--volumes"], cwd=ROOT,
                                       env=environment, stdout=output, stderr=subprocess.STDOUT, timeout=60, check=True)
                    errors.append("EmergencyTeardownRequired")
                if any(remaining_resources(manifest).values()):
                    errors.append("OwnedResourcesRemain")
            except Exception as error:
                errors.append(type(error).__name__)
        return errors


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--seconds", type=int, default=120, help="30..86400 seconds per server version")
    parser.add_argument("--seed", type=int, default=1729)
    parser.add_argument("--server-version", choices=VERSIONS, help="Limit a diagnostic run to one pinned version")
    args = parser.parse_args()
    if not MINIMUM_DURATION <= args.seconds <= MAXIMUM_DURATION or not 0 <= args.seed <= MAXIMUM_SEED:
        parser.error("Duration must be 30..86400 seconds and seed 0..2147483647")
    args.versions = (args.server_version,) if args.server_version else VERSIONS
    parent = Path(os.environ.get("ZKLINK_MATRIX_ARTIFACT_ROOT", tempfile.gettempdir())).resolve()
    parent.mkdir(parents=True, exist_ok=True)
    directory = Path(tempfile.mkdtemp(prefix="zklink-soak-supervisor.", dir=parent))
    supervisor = Supervisor(directory, args)
    print("Supervised soak artifacts: " + str(directory), flush=True)
    for number in (signal.SIGINT, signal.SIGTERM):
        signal.signal(number, supervisor.interrupt)
    inhibitor = None
    expected = sources()
    config = {"checkout": str(ROOT), "sourceSha256": expected, "runtimeImage": pinned_image(), "serverVersions": args.versions,
              "durationSecondsPerVersion": args.seconds, "seed": args.seed, "minimumHostFreeBytes": MINIMUM_HOST_FREE,
              "minimumVmFreeBytes": MINIMUM_VM_FREE, "maximumObservationGapSeconds": MAXIMUM_OBSERVATION_GAP,
              "checkIntervalSeconds": OBSERVATION_INTERVAL, "cleanupGuardSeconds": CLEANUP_GRACE,
              "releaseQualified": False, "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()}
    write_json(directory / "run.json", config)
    try:
        if sys.platform == "darwin":
            inhibitor = subprocess.Popen(["/usr/bin/caffeinate", "-i", "-w", str(os.getpid())])
            supervisor.inhibitor = inhibitor
            supervisor.save(idleSleepInhibitorPid=inhibitor.pid)
        else:
            supervisor.save(sleepGuard="continuity-observation")
        if args.seconds >= FULL_DURATION:
            supervisor.execute("preflight", PREFLIGHT_DURATION, expected)
        review = supervisor.execute("workload", args.seconds, expected)
        source_unchanged(expected)
        supervisor.stop_inhibitor()
        supervisor.save(state="passed", passed=True, review=review, finishedAtUtc=utc())
        return 0
    except BaseException as error:
        cleanup_errors = supervisor.cleanup()
        try:
            supervisor.stop_inhibitor()
        except Exception as cleanup:
            cleanup_errors.append(type(cleanup).__name__)
        supervisor.save(state="failed", passed=False,
                        failure=str(error) if isinstance(error, RuntimeError) else type(error).__name__,
                        cleanupErrors=cleanup_errors, finishedAtUtc=utc())
        print("Supervised soak failed; evidence retained at " + str(directory), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
