"""Content-addressed core candidate shared by managed and native consumers."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys

from release_version import validate_version

ROOT = Path(__file__).resolve().parent.parent
MANIFEST_NAME = "candidate.json"
SCHEMA_VERSION = 1
MAXIMUM_MANIFEST_BYTES = 1024 * 1024


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def runtime_inputs():
    paths = [ROOT / name for name in ["Directory.Build.props", "Directory.Packages.props", "global.json", "NuGet.Config"]]
    paths += [path for path in (ROOT / "src/ZkLink").rglob("*")
              if path.is_file() and path.suffix in {".cs", ".csproj", ".json"}
              and not {"obj", "bin"}.intersection(path.relative_to(ROOT).parts)]
    return {path.relative_to(ROOT).as_posix(): sha256(path) for path in sorted(paths)}


def write_candidate(directory, version, revision):
    version = validate_version(version)
    files = ["ZkLink." + version + suffix for suffix in [".nupkg", ".snupkg"]]
    manifest = {"schema_version": SCHEMA_VERSION, "version": version, "revision": revision,
                "runtime_inputs": runtime_inputs(), "files": {name: sha256(directory / name) for name in files}}
    target = directory / MANIFEST_NAME
    target.write_text(json.dumps(manifest, indent=2, sort_keys=True) + "\n", encoding="utf-8")
    return manifest


def read_candidate(directory, expected_manifest_sha256, revision=None):
    directory = Path(directory).resolve()
    manifest_path = directory / MANIFEST_NAME
    if manifest_path.is_symlink() or manifest_path.stat().st_size > MAXIMUM_MANIFEST_BYTES:
        raise ValueError("Unsafe candidate manifest")
    if not expected_manifest_sha256 or sha256(manifest_path) != expected_manifest_sha256.upper():
        raise ValueError("Candidate manifest identity mismatch")
    data = json.loads(manifest_path.read_text(encoding="utf-8"))
    version = validate_version(data["version"])
    if data["schema_version"] != SCHEMA_VERSION:
        raise ValueError("Unsupported candidate manifest")
    if revision is None:
        revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    if data["revision"] != revision or data["runtime_inputs"] != runtime_inputs():
        raise ValueError("Candidate revision or runtime inputs differ from this checkout")
    expected_files = {"ZkLink." + version + suffix for suffix in [".nupkg", ".snupkg"]}
    if set(data["files"]) != expected_files:
        raise ValueError("Expected exactly one core package and its symbols")
    for name, expected in data["files"].items():
        artifact = directory / name
        if artifact.is_symlink() or artifact.resolve().parent != directory or sha256(artifact) != expected:
            raise ValueError("Candidate package or symbol identity mismatch")
    return directory, data


def from_environment(revision):
    directory = os.environ.get("ZKLINK_CANDIDATE_DIRECTORY")
    digest = os.environ.get("ZKLINK_CANDIDATE_MANIFEST_SHA256")
    if not directory and not digest:
        return None
    if not directory or not digest:
        raise ValueError("Both candidate directory and manifest hash are required")
    return read_candidate(directory, digest, revision)


def export(directory):
    directory = Path(directory).resolve()
    digest = sha256(directory / MANIFEST_NAME)
    _, data = read_candidate(directory, digest)
    outputs = {"manifest-sha256": digest, "package-sha256": data["files"]["ZkLink." + data["version"] + ".nupkg"],
               "version": data["version"], "revision": data["revision"]}
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            for name, value in outputs.items():
                stream.write(name + "=" + value + "\n")
    print(json.dumps(outputs, sort_keys=True))


if __name__ == "__main__":
    if len(sys.argv) != 3 or sys.argv[1] != "export":
        raise SystemExit("Usage: release_candidate.py export <candidate-directory>")
    export(sys.argv[2])
