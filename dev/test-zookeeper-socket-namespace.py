#!/usr/bin/env python3
"""Verify socket ownership inside a fresh owned Docker DNS network namespace."""
import json
import os
import signal
from pathlib import Path
import subprocess
import tempfile
import uuid

from isolated_soak import ROOT, command, inspect_container, pinned_image, utc, write_json

BUILD_TIMEOUT = 180
TEST_TIMEOUT = 120
LABEL = "org.zklink.socket-probe"


def interrupt(_number, _frame):
    raise InterruptedError("Owned socket namespace fixture interrupted")


def main():
    signal.signal(signal.SIGTERM, interrupt)
    if os.getuid() == 0:
        raise RuntimeError("Run this fixture as a nonroot user")
    parent = Path(os.environ.get("ZKLINK_MATRIX_ARTIFACT_ROOT", tempfile.gettempdir())).resolve()
    parent.mkdir(parents=True, exist_ok=True)
    evidence = Path(tempfile.mkdtemp(prefix="zklink-socket-namespace.", dir=parent))
    print("Socket namespace artifacts: " + str(evidence), flush=True)
    with (evidence / "build.log").open("w") as log:
        for args in (["restore", "tests/ZkLink.FaultTests", "--locked-mode"],
                     ["build", "tests/ZkLink.FaultTests", "-c", "Release", "--no-restore", "-m:1", "-p:UseMicrosoftTestingPlatformRunner=true"]):
            subprocess.run(["dotnet", *args], cwd=ROOT, stdout=log, stderr=subprocess.STDOUT, timeout=BUILD_TIMEOUT, check=True)
    nonce = uuid.uuid4().hex
    name = "zklink-socket-namespace-" + nonce
    network = name + "-network"
    image = pinned_image()
    command("pull", image, timeout=BUILD_TIMEOUT)
    receipt = {"startedAtUtc": utc(), "name": name, "network": network, "runId": nonce, "image": image,
               "passed": False, "releaseQualified": False}
    try:
        receipt["networkId"] = command("network", "create", "--label", LABEL + "=" + nonce, network)
        binary = ROOT / "tests/ZkLink.FaultTests/bin/Release/net10.0"
        invocation = " && ".join("dotnet /test/ZkLink.FaultTests.dll --filter-trait Fixture=" + selection
                                 + " --output Detailed --show-stdout All --results-directory /evidence/" + selection
                                 for selection in ("SoakUnit", "SocketNamespace"))
        receipt["containerId"] = command("create", "--name", name, "--label", LABEL + "=" + nonce,
            "--network", network, "--user", f"{os.getuid()}:{os.getgid()}", "--workdir", "/tmp", "--ulimit", "core=0",
            "--env", "TESTINGPLATFORM_TELEMETRY_OPTOUT=1", "--env", "DOTNET_CLI_TELEMETRY_OPTOUT=1",
            "--env", "XDG_DATA_HOME=/tmp/zklink-data", "--env", "XDG_CONFIG_HOME=/tmp/zklink-config",
            "--mount", f"type=bind,source={binary},target=/test,readonly",
            "--mount", f"type=bind,source={evidence},target=/evidence", image, "sh", "-c", invocation)
        info = inspect_container(receipt["containerId"])
        if info["Config"]["Labels"].get(LABEL) != nonce or info["HostConfig"]["NetworkMode"] != network:
            raise RuntimeError("Socket fixture ownership or network mismatch")
        if info["HostConfig"]["Privileged"] or info["HostConfig"]["PortBindings"] or info["Config"]["User"] != f"{os.getuid()}:{os.getgid()}":
            raise RuntimeError("Socket fixture user, ports or privileges mismatch")
        if len(info["Mounts"]) != 2 or next(m for m in info["Mounts"] if m["Destination"] == "/test")["RW"]:
            raise RuntimeError("Socket fixture mounts mismatch")
        write_json(evidence / "run.json", receipt)
        with (evidence / "tests.log").open("w") as log:
            result = subprocess.run(["docker", "start", "--attach", receipt["containerId"]], stdout=log, stderr=subprocess.STDOUT, timeout=TEST_TIMEOUT)
        info = inspect_container(receipt["containerId"])
        receipt.update(exitCode=info["State"]["ExitCode"], attachExitCode=result.returncode)
        if result.returncode or info["State"]["Running"] or info["State"]["ExitCode"] or info["State"]["OOMKilled"]:
            raise RuntimeError("Socket ownership tests failed")
        receipt["passed"] = True
    finally:
        try:
            info = inspect_container(receipt.get("containerId", name), missing=True)
            if info is not None:
                if info["Config"]["Labels"].get(LABEL) != nonce or info["Name"].lstrip("/") != name:
                    raise RuntimeError("Socket fixture container ownership mismatch")
                command("container", "rm", "--force", info["Id"])
            network_info = command("network", "inspect", network, missing=True)
            if network_info is not None:
                value = json.loads(network_info)[0]
                if value["Labels"].get(LABEL) != nonce:
                    raise RuntimeError("Socket fixture network ownership mismatch")
                command("network", "rm", value["Id"])
            if inspect_container(receipt.get("containerId", name), missing=True) is not None or command("network", "ls", "--quiet", "--filter", "label=" + LABEL + "=" + nonce):
                raise RuntimeError("Socket fixture resources remain")
            receipt["cleanupVerified"] = True
        finally:
            receipt["finishedAtUtc"] = utc()
            write_json(evidence / "run.json", receipt)


if __name__ == "__main__":
    main()
