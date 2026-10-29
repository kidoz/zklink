#!/usr/bin/env python3
"""Run the native secure matrix on owned JVM fixtures on Windows, macOS or Linux."""

import argparse
import json
import os
import pathlib
import re
import secrets
import shutil
import signal
import sys
import tempfile

from aot_artifacts import ROOT, read_manifest, sha256
from portable_zookeeper import (
    FixtureError, JavaEnsemble, OwnedProcess, SERVER_VERSIONS, SECURITY_MODES, VOTER_COUNT,
    checked, export_java, generate_pki, java_tools, verify_runtime)

NATIVE_GUARD_SECONDS = 135
PUBLISH_GUARD_SECONDS = 900
EXPECTED_CASES = len(SERVER_VERSIONS) * len(SECURITY_MODES) * VOTER_COUNT


class FixtureInterrupted(BaseException):
    """Never let subprocess EINTR handling or startup OSError retries swallow cancellation."""


def interrupted(number, _frame):
    raise FixtureInterrupted("Portable fixture interrupted.")


def fixture_inputs():
    paths = [ROOT / "dev/portable_zookeeper.py", pathlib.Path(__file__),
             ROOT / "dev/run-native-smoke.py", ROOT / "tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.jar",
             ROOT / "tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.properties"]
    project = ROOT / "tests/ZkLink.JavaEnsemble"
    paths += [project / name for name in ["build.gradle.kts", "settings.gradle.kts", "gradle.lockfile", "gradle/verification-metadata.xml"]]
    paths += [path for path in (project / "src").rglob("*") if path.is_file()]
    return {str(path.relative_to(ROOT)): sha256(path) for path in paths}


def scrub_and_remove(private, run):
    if private.is_symlink() or not private.name.startswith("zklink-portable-private."):
        raise FixtureError("Refusing an unexpected private fixture directory.")
    leaked = []
    try:
        password_file = private / "server/store-password"
        password = password_file.read_bytes().strip() if password_file.exists() else None
        for path in run.rglob("*"):
            if path.is_file() and path.suffix in {".txt", ".log", ".json", ".properties", ".tsv", ".xml"}:
                content = path.read_bytes()
                if (password and password in content) or b"PRIVATE KEY-----" in content:
                    leaked.append(path.name)
                    path.unlink()
    finally:
        shutil.rmtree(private)
    if leaked:
        raise FixtureError("Sensitive fixture evidence was removed; verification failed.")


def execute(manifest_argument, run):
    initial_inputs = fixture_inputs()
    java, keytool = java_tools(run)
    if manifest_argument is None:
        checked([sys.executable, ROOT / "dev/prepare-aot.py"], run / "native-publish.txt", timeout=PUBLISH_GUARD_SECONDS)
        text = (run / "native-publish.txt").read_text(encoding="utf-8")
        match = re.search(r"Manifest: (.+)$", text, re.MULTILINE)
        if not match:
            raise FixtureError("Native publication did not produce its manifest.")
        manifest_argument = match.group(1)
    manifest, native = read_manifest(manifest_argument)
    runtime = export_java(java, run)
    # A new Gradle project writes its dependency locks only during deliberate bootstrap.
    if fixture_inputs() != initial_inputs:
        raise FixtureError("Portable fixture inputs changed during preparation.")
    private = pathlib.Path(tempfile.mkdtemp(prefix="zklink-portable-private."))
    active = None
    completed = []
    cleanup_errors = []
    cleanup_records = []
    failure = None
    try:
        generate_pki(keytool, private, run)
        for version in SERVER_VERSIONS:
            for mode in SECURITY_MODES:
                active = JavaEnsemble(java, runtime, private, run, version, mode)
                print(f"Starting portable ZooKeeper {version} {mode}", flush=True)
                active.start()
                for index, node in enumerate(active.nodes[:VOTER_COUNT]):
                    environment = os.environ.copy()
                    environment.update(ZKLINK_AOT_ENDPOINT=f"127.0.0.1:{node['secure']}",
                                       ZKLINK_AOT_SERVER_VERSION=version, ZKLINK_AOT_SECURITY_MODE=mode,
                                       ZKLINK_AOT_CERTIFICATE_DIRECTORY=str(private),
                                       ZKLINK_AOT_PLAINTEXT_ENDPOINT=f"127.0.0.1:{active.nodes[0]['plain']}",
                                       ZKLINK_AOT_EXPIRED_ENDPOINT=f"127.0.0.1:{active.nodes[-1]['secure']}",
                                       ZKLINK_USER="zklink-portable-native", ZKLINK_PASSWORD=secrets.token_hex(24))
                    key = active.key + "-" + node["name"]
                    options = ["--security-negatives"] if index == 0 else []
                    server_log = run / (active.key + "-" + node["name"] + "-server.log")
                    log_start = server_log.stat().st_size
                    output = run / (key + "-native.txt")
                    child = OwnedProcess([sys.executable, ROOT / "dev/run-native-smoke.py", manifest, *options], output, environment)
                    code = child.wait(NATIVE_GUARD_SECONDS)
                    (run / (key + "-exit.json")).write_text(json.dumps({"exitCode": code}) + "\n", encoding="utf-8")
                    if code != 0 or "Native smoke: PASS" not in output.read_text(encoding="utf-8"):
                        raise FixtureError("A direct native portable case failed.")
                    if mode == "mtls" and index == 0:
                        with server_log.open("rb") as source:
                            source.seek(log_start)
                            proof = source.read()
                        if ("Native missing-client-certificate connection rejected" not in output.read_text(encoding="utf-8")
                                or b"Empty client certificate chain" not in proof):
                            raise FixtureError("Missing-client-certificate rejection lacks fresh server evidence.")
                        (run / (key + "-certificate-rejection.json")).write_text(json.dumps({"nativeRejectionObserved": True, "freshServerRejectionObserved": True}) + "\n", encoding="utf-8")
                    completed.append({"server": version, "mode": mode, "node": node["name"], "nativeSha256": native["consumers"]["ZkLink.AotSmoke"]["sha256"]})
                active.probe_all("final")
                active.close()
                cleanup_records.append(active.key)
                active = None
                print(f"Portable native {native['rid']} on ZooKeeper {version} {mode}: PASS", flush=True)
        verify_runtime(runtime, run)
        if len(completed) != EXPECTED_CASES or fixture_inputs() != initial_inputs:
            raise FixtureError("Portable matrix is incomplete or its inputs changed.")
    except BaseException as error:
        failure = error
    finally:
        if active is not None:
            try:
                active.probe_all("final")
            except Exception as error:
                cleanup_errors.append("Final readiness: " + type(error).__name__)
            finally:
                try:
                    active.close()
                except Exception as error:
                    cleanup_errors.append("Owned process cleanup: " + type(error).__name__)
                cleanup_records.append(active.key)
        try:
            scrub_and_remove(private, run)
        except Exception as error:
            cleanup_errors.append("Private cleanup: " + type(error).__name__)
    receipt = {"rid": native["rid"], "nativeManifest": str(manifest), "nativeManifestSha256": sha256(manifest),
               "fixtureBackend": "owned Java quorum / NIO / JSSE", "inputs": initial_inputs,
               "nativeCases": completed, "cleanedFixtures": cleanup_records,
               "privateDirectoryRemoved": not private.exists(), "cleanupErrors": cleanup_errors,
               "failureType": type(failure).__name__ if failure is not None else None,
               "passed": failure is None and not cleanup_errors and len(completed) == EXPECTED_CASES}
    (run / "portable-result.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    if failure is not None:
        raise failure
    if not receipt["passed"]:
        raise FixtureError("Portable native matrix cleanup failed.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--native-manifest", type=pathlib.Path, help="Reuse a matching candidate with unchanged inputs.")
    arguments = parser.parse_args()
    artifacts = ROOT / "artifacts/portable-aot"
    artifacts.mkdir(parents=True, exist_ok=True)
    run = pathlib.Path(tempfile.mkdtemp(prefix="run.", dir=artifacts))
    print(f"Portable native artifacts: {run}", flush=True)
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        execute(arguments.native_manifest, run)
        return 0
    except BaseException as error:
        # Report the type, not platform error text or fixture credentials.
        detail = ": " + str(error) if isinstance(error, FixtureError) else ""
        print("Portable native verification failed: " + type(error).__name__ + detail, file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
