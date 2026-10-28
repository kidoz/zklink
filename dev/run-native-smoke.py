#!/usr/bin/env python3
"""Directly execute a verified native consumer with a bounded owned process lifetime."""

import os
import pathlib
import signal
import subprocess
import sys

from aot_artifacts import read_manifest

PROCESS_GUARD_SECONDS = 120
STOP_GRACE_SECONDS = 5


def interrupted(number, _frame):
    raise SystemExit(128 + number)


def main():
    if len(sys.argv) not in {2, 3} or (len(sys.argv) == 3 and sys.argv[2] not in {"--security-negatives", "--native-check"}):
        raise SystemExit("Usage: run-native-smoke.py <native.json> [--security-negatives|--native-check]")
    manifest, data = read_manifest(sys.argv[1])
    consumer = data["consumers"]["ZkLink.AotSmoke"]
    binary = manifest.parent / consumer["executable"]
    environment = os.environ.copy()
    environment["ZKLINK_AOT_EXPECTED_RID"] = data["rid"]
    environment["ZKLINK_AOT_EXECUTABLE_SHA256"] = consumer["sha256"]
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    windows = os.name == "nt"
    process = subprocess.Popen([str(binary), *sys.argv[2:]], env=environment,
                               start_new_session=not windows,
                               creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if windows else 0)
    try:
        try:
            result = process.wait(timeout=PROCESS_GUARD_SECONDS)
            return result if result >= 0 else 128 - result
        except subprocess.TimeoutExpired:
            print("Native consumer process deadline exceeded.", flush=True)
            return 124
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        if process.poll() is None:
            if windows:
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, check=False)
            else:
                try:
                    os.killpg(process.pid, signal.SIGTERM)
                except ProcessLookupError:
                    pass
            try:
                process.wait(timeout=STOP_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                if not windows:
                    try:
                        os.killpg(process.pid, signal.SIGKILL)
                    except ProcessLookupError:
                        pass
                else:
                    process.kill()
                process.wait()


if __name__ == "__main__":
    raise SystemExit(main())
