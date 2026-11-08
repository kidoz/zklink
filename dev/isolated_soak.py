"""Owned runtime container for the soak fixture; no Docker access inside the test."""
import argparse
import datetime
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parent.parent
COMMAND_TIMEOUT = 15
PULL_TIMEOUT = 180
PROCESS_OVERHEAD = 150
PROCESS_REAP_TIMEOUT = 10
MAXIMUM_COMMAND_BYTES = 262144
RUN_LABEL = "org.zklink.fault-run"
PROJECT_LABEL = "org.zklink.soak.project"
ROLE_LABEL = "org.zklink.soak.role"
VERSIONS = ("3.8.7", "3.9.6")
MINIMUM_DURATION = 30
MAXIMUM_DURATION = 86400
MAXIMUM_SEED = 2147483647
POLL_SECONDS = 0.2


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def write_json(path, value):
    temporary = path.with_suffix(path.suffix + ".tmp")
    temporary.write_text(json.dumps(value, indent=2) + "\n")
    temporary.replace(path)


def command(*args, timeout=COMMAND_TIMEOUT, missing=False):
    result = subprocess.run(["docker", *args], capture_output=True, text=True, timeout=timeout)
    if len(result.stdout.encode()) + len(result.stderr.encode()) > MAXIMUM_COMMAND_BYTES:
        raise RuntimeError("Docker observation exceeded its output bound")
    if result.returncode:
        if missing and ("No such container:" in result.stderr or "No such object:" in result.stderr):
            return None
        # Do not retain arbitrary daemon output or environment values in diagnostics.
        raise RuntimeError("Docker command failed: " + args[0])
    return result.stdout.strip()


def inspect_container(identity, missing=False):
    result = command("container", "inspect", identity, missing=missing)
    return None if result is None else json.loads(result)[0]


def validate_identity(manifest):
    version, nonce = manifest["serverVersion"], manifest["runId"]
    if version not in VERSIONS or not re.fullmatch(r"[0-9a-f]{32}", nonce):
        raise RuntimeError("Invalid owned fixture identity")
    project = "zklink-soak-fault-" + version.replace(".", "-") + "-" + nonce
    if manifest["project"] != project or manifest["containerName"] != project + "-driver":
        raise RuntimeError("Invalid owned runtime name")


def validate_owner(info, manifest):
    validate_identity(manifest)
    labels = info["Config"].get("Labels") or {}
    expected = {RUN_LABEL: manifest["runId"], PROJECT_LABEL: manifest["project"], ROLE_LABEL: "driver"}
    if any(labels.get(key) != value for key, value in expected.items()):
        raise RuntimeError("Runtime container ownership mismatch")
    if info["Name"].lstrip("/") != manifest["containerName"]:
        raise RuntimeError("Runtime container name mismatch")
    if manifest.get("containerId") and info["Id"] != manifest["containerId"]:
        raise RuntimeError("Runtime container identity changed")


def validate_runtime(info, manifest):
    validate_owner(info, manifest)
    host = info["HostConfig"]
    if host["NetworkMode"] != "container:" + manifest["namespaceContainerId"]:
        raise RuntimeError("Runtime network namespace mismatch")
    if host.get("PortBindings") or host.get("PublishAllPorts") or host.get("Privileged"):
        raise RuntimeError("Runtime exposed ports or privileges")
    if info["Image"] != manifest["imageId"] or info["Config"]["User"] != manifest["user"]:
        raise RuntimeError("Runtime image or user mismatch")
    mounts = info.get("Mounts", [])
    if len(mounts) != 2 or any(mount["Type"] != "bind" for mount in mounts):
        raise RuntimeError("Runtime mount count or type mismatch")
    expected = {(manifest["binaryDirectory"], "/test", False),
                (manifest["evidenceDirectory"], manifest["containerEvidenceDirectory"], True)}
    if {(mount["Source"], mount["Destination"], mount["RW"]) for mount in mounts} != expected:
        raise RuntimeError("Runtime mounts mismatch")


def remove_owned(manifest):
    validate_identity(manifest)
    info = inspect_container(manifest.get("containerId") or manifest["containerName"], missing=True)
    if info is not None:
        validate_owner(info, manifest)
        command("container", "rm", "--force", info["Id"])
        if inspect_container(info["Id"], missing=True) is not None:
            raise RuntimeError("Owned runtime removal was not confirmed")


def cleanup_file(path):
    manifest = json.loads(path.read_text())
    remove_owned(manifest)
    # The worker is the sole writer. The EXIT fallback only removes resources.


def binary_hashes(directory):
    return {str(path.relative_to(directory)): hashlib.sha256(path.read_bytes()).hexdigest()
            for path in sorted(directory.rglob("*")) if path.is_file()}


def validate_binaries(manifest):
    if binary_hashes(Path(manifest["binaryDirectory"])) != manifest["binarySha256"]:
        raise RuntimeError("Mounted test binaries changed during execution")


def pinned_image():
    values = [line.split("=", 1)[1] for line in (ROOT / "dev/dotnet-soak-runtime.env").read_text().splitlines()
              if line.startswith("ZKLINK_SOAK_RUNTIME_IMAGE=")]
    if len(values) != 1 or not re.fullmatch(r"mcr\.microsoft\.com/dotnet/runtime:10\.0\.\d+@sha256:[0-9a-f]{64}", values[0]):
        raise RuntimeError("Missing immutable runtime pin")
    return values[0]


def member(identity, project, nonce, require_running=True):
    info = inspect_container(identity)
    labels = info["Config"].get("Labels") or {}
    if (labels.get(RUN_LABEL) != nonce or labels.get("com.docker.compose.project") != project
            or labels.get("com.docker.compose.service") not in ("zoo1", "zoo2", "zoo3")
            or (require_running and not info["State"]["Running"])):
        raise RuntimeError("Ensemble member ownership or liveness mismatch")
    return info


def run(args):
    evidence = args.evidence.resolve()
    if not evidence.is_dir() or not evidence.name.startswith("zklink-soak-faults."):
        raise RuntimeError("Expected an owned soak evidence directory")
    if os.getuid() == 0:
        raise RuntimeError("The isolated workload must run as a nonroot user")
    image = pinned_image()
    command("pull", image, timeout=PULL_TIMEOUT)
    image_info = json.loads(command("image", "inspect", image))[0]
    if image_info["Os"] != "linux" or image_info["Architecture"] not in ("amd64", "arm64"):
        raise RuntimeError("Unsupported isolated runtime platform")
    target = member(args.target, args.project, args.nonce)
    leader = member(args.leader, args.project, args.nonce)
    networks = leader["NetworkSettings"]["Networks"]
    if len(networks) != 1 or set(target["NetworkSettings"]["Networks"]) != set(networks):
        raise RuntimeError("Expected one shared owned ensemble network")
    leader_ip = next(iter(networks.values()))["IPAddress"]
    import ipaddress
    ipaddress.IPv4Address(leader_ip)
    binaries = ROOT / "tests/ZkLink.FaultTests/bin/Release/net10.0"
    if not (binaries / "ZkLink.FaultTests.dll").is_file():
        raise RuntimeError("Missing built test consumer")
    container_evidence = "/evidence/" + evidence.name
    manifest = {"serverVersion": args.version, "runId": args.nonce, "project": args.project,
                "containerName": args.project + "-driver", "namespaceContainerId": target["Id"],
                "maintenanceContainerId": leader["Id"], "image": image, "imageId": image_info["Id"],
                "platform": "linux-" + {"amd64": "x64", "arm64": "arm64"}[image_info["Architecture"]], "user": f"{os.getuid()}:{os.getgid()}",
                "binaryDirectory": str(binaries), "evidenceDirectory": str(evidence),
                "containerEvidenceDirectory": container_evidence, "durationSeconds": args.seconds,
                "seed": args.seed, "startedAtUtc": utc(), "state": "planned", "releaseQualified": False,
                "binarySha256": binary_hashes(binaries)}
    validate_identity(manifest)
    path = evidence / (args.version + "-driver.json")
    write_json(path, manifest)
    interrupted = False

    def interrupt(_number, _frame):
        nonlocal interrupted
        interrupted = True

    old_handlers = {number: signal.signal(number, interrupt) for number in (signal.SIGINT, signal.SIGTERM)}
    process = None
    try:
        environment = {"TESTINGPLATFORM_TELEMETRY_OPTOUT": "1", "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
                       "XDG_DATA_HOME": "/tmp/zklink-test-data", "XDG_CONFIG_HOME": "/tmp/zklink-test-config",
                       "ZKLINK_FAULT_ROLE": "soak", "ZKLINK_TEST_SERVER_VERSION": args.version,
                       "ZKLINK_FAULT_ENDPOINT": "127.0.0.1:2181", "ZKLINK_SOAK_MAINTENANCE_ENDPOINT": leader_ip + ":2181",
                       "ZKLINK_SOAK_DURATION_SECONDS": str(args.seconds), "ZKLINK_SOAK_SEED": str(args.seed),
                       "ZKLINK_SOAK_RESULT_FILE": container_evidence + "/" + args.version + "-soak.jsonl"}
        create = ["create", "--name", manifest["containerName"], "--network", "container:" + target["Id"],
                  "--user", manifest["user"], "--workdir", "/tmp", "--ulimit", "core=0",
                  "--label", RUN_LABEL + "=" + args.nonce, "--label", PROJECT_LABEL + "=" + args.project,
                  "--label", ROLE_LABEL + "=driver",
                  "--mount", f"type=bind,source={binaries},target=/test,readonly",
                  "--mount", f"type=bind,source={evidence},target={container_evidence}"]
        for key, value in environment.items():
            create.extend(["--env", key + "=" + value])
        create.extend([image, "dotnet", "/test/ZkLink.FaultTests.dll", "--filter-trait", "Fixture=Soak",
                       "--output", "Detailed", "--show-stdout", "All", "--show-live-output", "on",
                       "--timeout", str(args.seconds + PROCESS_OVERHEAD) + "s",
                       "--results-directory", container_evidence + "/" + args.version + "-results"])
        manifest["containerId"] = command(*create)
        validate_runtime(inspect_container(manifest["containerId"]), manifest)
        manifest.update(state="created", createdAtUtc=utc())
        write_json(path, manifest)
        process = subprocess.Popen(["docker", "start", "--attach", manifest["containerId"]])
        manifest.update(state="running", attachedAtUtc=utc())
        write_json(path, manifest)
        deadline = time.monotonic() + args.seconds + PROCESS_OVERHEAD
        while process.poll() is None:
            if interrupted:
                raise RuntimeError("Owned runtime interrupted")
            if time.monotonic() > deadline:
                raise RuntimeError("Owned runtime exceeded its wall-time guard")
            time.sleep(POLL_SECONDS)
        validate_binaries(manifest)
        info = inspect_container(manifest["containerId"])
        validate_runtime(info, manifest)
        manifest.update(exitCode=info["State"]["ExitCode"], attachExitCode=process.returncode)
        if interrupted or info["State"]["Running"] or info["State"]["OOMKilled"] or info["State"]["ExitCode"] or process.returncode:
            raise RuntimeError("Owned runtime failed or did not stop")
        manifest["state"] = "passed"
    except BaseException as error:
        manifest.update(state="failed", failure=str(error) if isinstance(error, RuntimeError) else type(error).__name__)
        raise
    finally:
        write_json(path, manifest)
        try:
            remove_owned(manifest)
            manifest["cleanupVerified"] = True
        except Exception as error:
            manifest["cleanupError"] = type(error).__name__
            raise
        finally:
            if process is not None and process.poll() is None:
                process.terminate()
                try:
                    process.wait(timeout=PROCESS_REAP_TIMEOUT)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=PROCESS_REAP_TIMEOUT)
            manifest["finishedAtUtc"] = utc()
            write_json(path, manifest)
            for number, handler in old_handlers.items():
                signal.signal(number, handler)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--cleanup", type=Path)
    parser.add_argument("--evidence", type=Path)
    parser.add_argument("--version", choices=VERSIONS)
    parser.add_argument("--project")
    parser.add_argument("--nonce")
    parser.add_argument("--target")
    parser.add_argument("--leader")
    parser.add_argument("--seconds", type=int)
    parser.add_argument("--seed", type=int)
    args = parser.parse_args()
    if args.cleanup:
        cleanup_file(args.cleanup)
    else:
        if any(getattr(args, name) is None for name in ("evidence", "version", "project", "nonce", "target", "leader", "seconds", "seed")):
            parser.error("All worker settings are required")
        if not MINIMUM_DURATION <= args.seconds <= MAXIMUM_DURATION or not 0 <= args.seed <= MAXIMUM_SEED:
            parser.error("Invalid duration or seed")
        run(args)


if __name__ == "__main__":
    try:
        main()
    except Exception as failure:
        print(str(failure) if isinstance(failure, RuntimeError) else type(failure).__name__, file=sys.stderr)
        sys.exit(1)
