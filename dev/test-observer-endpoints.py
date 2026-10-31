#!/usr/bin/env python3
"""Verify observer connections on isolated pinned ensembles and retain cleanup evidence."""

import datetime
import hashlib
import json
import os
from pathlib import Path
import secrets
import signal
import subprocess
import tempfile

ROOT = Path(__file__).resolve().parent.parent
SERVICES = ["zoo1", "zoo2", "zoo3", "zoo4"]
COMMAND_GUARD_SECONDS = 120
CLEANUP_GUARD_SECONDS = 60
INTERRUPTED = False


def interrupt(_number, _frame):
    global INTERRUPTED
    if not INTERRUPTED:
        INTERRUPTED = True
        raise KeyboardInterrupt("Observer verification interrupted")


def main():
    artifacts = ROOT / "artifacts" / "observers"
    artifacts.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="run.", dir=artifacts))
    print("Observer evidence: " + str(run), flush=True)
    environment = os.environ.copy()
    environment.pop("ZOOKEEPER_IMAGE", None)
    inputs = [ROOT / "compose.observer.yaml", Path(__file__),
              ROOT / "dev/zookeeper-healthcheck.sh",
              ROOT / "tests/ZkLink.IntegrationTests/ObserverEndpointTests.cs",
              ROOT / "tests/ZkLink.IntegrationTests/WatchReconnectProxy.cs",
              ROOT / "tests/ZkLink.IntegrationTests/LiveZooKeeperFixture.cs",
              ROOT / "dev/zookeeper-3.8.7.env", ROOT / "dev/zookeeper-3.9.6.env"]
    receipt = {"started_at_utc": datetime.datetime.now(datetime.timezone.utc).isoformat(),
               "revision": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip(),
               "inputs": {str(path.relative_to(ROOT)): hashlib.sha256(path.read_bytes()).hexdigest() for path in inputs},
               "versions": [], "passed": False}

    def command(arguments, name, guard=COMMAND_GUARD_SECONDS):
        with (run / name).open("w") as output:
            result = subprocess.run(arguments, cwd=ROOT, env=environment,
                                    stdout=output, stderr=subprocess.STDOUT, timeout=guard)
        if result.returncode:
            raise RuntimeError("Observer command failed: " + name)

    try:
        command(["dotnet", "restore", "tests/ZkLink.IntegrationTests", "--locked-mode"], "restore.log")
        command(["dotnet", "build", "tests/ZkLink.IntegrationTests", "-c", "Release", "--no-restore"], "build.log")
        for version in ["3.8.7", "3.9.6"]:
            nonce = secrets.token_hex(16)
            environment["ZKLINK_OBSERVER_RUN_ID"] = nonce
            project = "zklink-observer-" + version.replace(".", "-") + "-" + nonce
            prefix = ["docker", "compose", "-f", "compose.observer.yaml", "--env-file",
                      "dev/zookeeper-" + version + ".env", "--project-name", project]
            entry = {"version": version, "project": project, "passed": False, "cleanup_verified": False}
            receipt["versions"].append(entry)
            try:
                command(prefix + ["config"], version + "-compose.yaml")
                command(prefix + ["up", "-d", "--wait", "--wait-timeout", "90"], version + "-startup.log")
                roles = []
                endpoints = []
                for service in SERVICES:
                    command(prefix + ["exec", "-T", service, "zkServer.sh", "version"], version + "-" + service + "-version.log")
                    if "Apache ZooKeeper, version " + version + " " not in (run / (version + "-" + service + "-version.log")).read_text():
                        raise RuntimeError("Observer fixture version mismatch")
                    command(prefix + ["exec", "-T", service, "bash", "/opt/zklink/zookeeper-healthcheck.sh"], version + "-" + service + "-ready.log")
                    role_file = version + "-" + service + "-role.log"
                    command(prefix + ["exec", "-T", service, "zkServer.sh", "status"], role_file)
                    roles.append((run / role_file).read_text())
                    endpoints.append(subprocess.check_output(prefix + ["port", service, "2181"], cwd=ROOT, env=environment, text=True, timeout=10).strip())
                if (sum("Mode: leader" in role for role in roles[:3]) != 1
                        or sum("Mode: follower" in role for role in roles[:3]) != 2
                        or "Mode: observer" not in roles[3]):
                    raise RuntimeError("Expected three voters and one observer")
                environment.update(ZKLINK_TEST_CONNECTION_STRING=",".join(endpoints[:3]),
                                   ZKLINK_TEST_OBSERVER_ENDPOINT=endpoints[3], ZKLINK_TEST_SERVER_VERSION=version)
                command(["dotnet", "test", "--project", "tests/ZkLink.IntegrationTests", "-c", "Release",
                         "--no-build", "--no-restore", "--filter-trait", "Fixture=Observer", "--timeout", "90s",
                         "--results-directory", str(run / (version + "-results"))], version + "-tests.log")
                entry["passed"] = True
            finally:
                try:
                    for service in SERVICES:
                        command(prefix + ["exec", "-T", service, "bash", "/opt/zklink/zookeeper-healthcheck.sh"],
                                version + "-" + service + "-rollback.log", CLEANUP_GUARD_SECONDS)
                finally:
                    try:
                        command(prefix + ["logs", "--no-color"], version + "-server.log")
                    finally:
                        command(prefix + ["down", "--volumes"], version + "-cleanup.log", CLEANUP_GUARD_SECONDS)
                for listing in [["docker", "ps", "-aq"], ["docker", "network", "ls", "-q"], ["docker", "volume", "ls", "-q"]]:
                    remaining = subprocess.check_output(listing + ["--filter", "label=com.docker.compose.project=" + project], text=True, timeout=10).strip()
                    if remaining:
                        raise RuntimeError("Owned observer resources remain")
                entry["cleanup_verified"] = True
        receipt["passed"] = all(entry["passed"] and entry["cleanup_verified"] for entry in receipt["versions"])
    finally:
        receipt["inputs_unchanged"] = all(hashlib.sha256((ROOT / name).read_bytes()).hexdigest() == digest
                                            for name, digest in receipt["inputs"].items())
        receipt["passed"] = receipt["passed"] and receipt["inputs_unchanged"]
        receipt["finished_at_utc"] = datetime.datetime.now(datetime.timezone.utc).isoformat()
        (run / "observer.json").write_text(json.dumps(receipt, indent=2) + "\n")
    if not receipt["passed"]:
        raise RuntimeError("Observer qualification did not pass")


if __name__ == "__main__":
    signal.signal(signal.SIGTERM, interrupt)
    signal.signal(signal.SIGINT, interrupt)
    main()
