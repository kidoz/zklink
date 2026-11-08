"""Prepare an exact, checksum-verified Temurin archive for setup-java/jdkfile."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess
import time


PINS = Path(__file__).with_name("temurin.json")
ATTEMPTS = 3
ATTEMPT_SECONDS = 90


def select_pin(runner_os, runner_arch):
    systems = {"Linux": "linux", "Windows": "win", "macOS": "osx"}
    architectures = {"X64": "x64", "ARM64": "arm64"}
    pins = json.loads(PINS.read_text(encoding="utf-8"))
    try:
        rid = f"{systems[runner_os]}-{architectures[runner_arch]}"
        return pins["java_version"], rid, pins["assets"][rid]
    except KeyError as error:
        raise ValueError(f"No pinned JDK for {runner_os}/{runner_arch}") from error


def verified(path, pin):
    if not path.is_file() or path.stat().st_size != pin["bytes"]:
        return False
    with path.open("rb") as archive:
        return hashlib.file_digest(archive, "sha256").hexdigest() == pin["sha256"]


def ensure_archive(path, pin, *, run=subprocess.run, sleep=time.sleep):
    # A cache hit is a performance hint, never an integrity check.
    if verified(path, pin):
        print(f"Verified cached JDK: {path.name}", flush=True)
        return
    path.parent.mkdir(parents=True, exist_ok=True)
    path.unlink(missing_ok=True)
    partial = path.with_name(path.name + ".part")
    for attempt in range(ATTEMPTS):
        partial.unlink(missing_ok=True)
        # Both routes serve the same pinned GitHub release asset. The browser
        # route also works when the unauthenticated API rate limit is exhausted.
        url = pin["download_url"] if attempt == 1 else pin["api_url"]
        print(f"Downloading {path.name}, attempt {attempt + 1}/{ATTEMPTS}", flush=True)
        try:
            run([
                "curl", "--fail", "--location", "--silent", "--show-error",
                "--connect-timeout", "10", "--max-time", str(ATTEMPT_SECONDS),
                "--max-filesize", str(pin["bytes"]),
                "--header", "Accept: application/octet-stream",
                "--output", str(partial), url,
            ], check=True, timeout=ATTEMPT_SECONDS + 5)
            if not verified(partial, pin):
                raise ValueError("JDK archive size or SHA-256 did not match the pin")
            partial.replace(path)
            print(f"Verified downloaded JDK: {path.name}", flush=True)
            return
        except (subprocess.SubprocessError, OSError, ValueError) as error:
            print(f"JDK attempt {attempt + 1} failed: {error}", flush=True)
        finally:
            partial.unlink(missing_ok=True)
        if attempt + 1 < ATTEMPTS:
            sleep(2 ** (attempt + 1))
    raise RuntimeError(f"Cannot acquire verified JDK after {ATTEMPTS} attempts")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["describe", "ensure"])
    args = parser.parse_args()
    version, rid, pin = select_pin(os.environ["RUNNER_OS"], os.environ["RUNNER_ARCH"])
    directory = Path(os.environ["RUNNER_TEMP"]) / "zklink-jdk" / pin["sha256"]
    archive = directory / pin["name"]
    if args.command == "ensure":
        ensure_archive(archive, pin)
    else:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"archive={archive}\n")
            output.write(f"cache-key=temurin-archive-v1-{rid}-{version}-{pin['sha256']}\n")
            output.write(f"java-version={version}\n")


if __name__ == "__main__":
    main()
