"""Prepare checksum-verified Temurin and Gradle archives for CI fixtures."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
from urllib.parse import urlsplit


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


def select_gradle_pin(root):
    pin = json.loads(PINS.with_name("gradle.json").read_text(encoding="utf-8"))
    properties = root / "tests/ZkLink.JavaOracle/gradle/wrapper/gradle-wrapper.properties"
    settings = dict(line.split("=", 1) for line in properties.read_text(encoding="utf-8").splitlines()
                    if "=" in line and not line.startswith("#"))
    expected = {
        "distributionUrl": pin["wrapper_url"].replace(":", r"\:"),
        "distributionSha256Sum": pin["sha256"],
        "distributionBase": "GRADLE_USER_HOME", "distributionPath": "wrapper/dists",
        "zipStoreBase": "GRADLE_USER_HOME", "zipStorePath": "wrapper/dists",
    }
    if any(settings.get(key) != value for key, value in expected.items()):
        raise ValueError("Gradle download pin does not match the wrapper configuration")
    return pin


def wrapper_archive_path(url, gradle_home):
    # Match Gradle 9.8.2 PathAssembler: unsigned MD5(URL), encoded in base 36.
    # MD5 is only a directory identifier; archive integrity uses SHA-256.
    value = int.from_bytes(hashlib.md5(url.encode("utf-8"), usedforsecurity=False).digest(), "big")
    encoded = ""
    while value:
        value, digit = divmod(value, 36)
        encoded = "0123456789abcdefghijklmnopqrstuvwxyz"[digit] + encoded
    name = Path(urlsplit(url).path).name
    return gradle_home / "wrapper/dists" / name.removesuffix(".zip") / (encoded or "0") / name


def seed_gradle_archive(archive, pin, gradle_home):
    if not verified(archive, pin):
        raise ValueError("Cannot seed Gradle from an unverified archive")
    destination = wrapper_archive_path(pin["wrapper_url"], gradle_home)
    destination.parent.mkdir(parents=True, exist_ok=True)
    partial = destination.with_name(destination.name + ".seed-part")
    try:
        shutil.copyfile(archive, partial)
        partial.replace(destination)
    finally:
        partial.unlink(missing_ok=True)
    # Do not create .ok or unpack the ZIP: the real wrapper must verify its
    # configured SHA-256, validate the distribution and perform installation.
    print(f"Seeded verified Gradle wrapper archive: {destination}", flush=True)


def ensure_archive(path, pin, *, run=subprocess.run, sleep=time.sleep):
    # A cache hit is a performance hint, never an integrity check.
    if verified(path, pin):
        print(f"Verified cached archive: {path.name}", flush=True)
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
                raise ValueError("Archive size or SHA-256 did not match the pin")
            partial.replace(path)
            print(f"Verified downloaded archive: {path.name}", flush=True)
            return
        except (subprocess.SubprocessError, OSError, ValueError) as error:
            print(f"Archive attempt {attempt + 1} failed: {error}", flush=True)
        finally:
            partial.unlink(missing_ok=True)
        if attempt + 1 < ATTEMPTS:
            sleep(2 ** (attempt + 1))
    raise RuntimeError(f"Cannot acquire verified archive after {ATTEMPTS} attempts")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=["describe", "ensure", "describe-gradle", "ensure-gradle"])
    args = parser.parse_args()
    gradle = args.command.endswith("-gradle")
    if gradle:
        pin = select_gradle_pin(Path(os.environ["GITHUB_WORKSPACE"]))
        cache_key = f"gradle-archive-v1-{pin['name']}-{pin['sha256']}"
    else:
        version, rid, pin = select_pin(os.environ["RUNNER_OS"], os.environ["RUNNER_ARCH"])
        cache_key = f"temurin-archive-v1-{rid}-{version}-{pin['sha256']}"
    directory = Path(os.environ["RUNNER_TEMP"]) / ("zklink-gradle" if gradle else "zklink-jdk") / pin["sha256"]
    archive = directory / pin["name"]
    if args.command.startswith("ensure"):
        ensure_archive(archive, pin)
        if gradle:
            gradle_home = Path(os.environ.get("GRADLE_USER_HOME", str(Path.home() / ".gradle")))
            seed_gradle_archive(archive, pin, gradle_home)
    else:
        with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
            output.write(f"archive={archive}\n")
            output.write(f"cache-key={cache_key}\n")
            if not gradle:
                output.write(f"java-version={version}\n")


if __name__ == "__main__":
    main()
