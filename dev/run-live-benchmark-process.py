#!/usr/bin/env python3
"""Bound one owned benchmark process group, including on interrupted runners."""

import os
import signal
import subprocess
import sys

MINIMUM_GUARD_SECONDS = 91
MAXIMUM_GUARD_SECONDS = 120
STOP_GRACE_SECONDS = 5


def interrupted(number, _frame):
    raise SystemExit(128 + number)


def main():
    guard = int(sys.argv[1])
    if not MINIMUM_GUARD_SECONDS <= guard <= MAXIMUM_GUARD_SECONDS:
        raise SystemExit("Invalid bounded benchmark process guard.")
    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    process = subprocess.Popen(sys.argv[2:], start_new_session=True)
    try:
        try:
            result = process.wait(timeout=guard)
            exit_code = result if result >= 0 else 128 - result
            if exit_code:
                print(f"Live benchmark process exited with code {exit_code}.", flush=True)
            return exit_code
        except subprocess.TimeoutExpired:
            print("Live benchmark process deadline exceeded.", flush=True)
            return 124
    finally:
        signal.signal(signal.SIGTERM, signal.SIG_IGN)
        signal.signal(signal.SIGINT, signal.SIG_IGN)
        if process.poll() is None:
            try:
                os.killpg(process.pid, signal.SIGTERM)
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=STOP_GRACE_SECONDS)
            except subprocess.TimeoutExpired:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                process.wait()


if __name__ == "__main__":
    raise SystemExit(main())
