#!/usr/bin/env python3
"""Download verified release assets; publication never repacks the candidate."""

import importlib.util
import json
import os
from pathlib import Path
import re
import stat
import subprocess
import tempfile
import zipfile

from release_candidate import ROOT, MANIFEST_NAME, read_candidate, sha256
from release_version import validate_version

MAXIMUM_EVIDENCE_ENTRIES = 10_000
MAXIMUM_EVIDENCE_BYTES = 512 * 1024 * 1024
MAXIMUM_INDEX_BYTES = 1024 * 1024
COMMAND_GUARD_SECONDS = 120


def gh(*arguments, capture=False):
    result = subprocess.run(["gh", *arguments], cwd=ROOT, check=True, text=True,
                            stdout=subprocess.PIPE if capture else None, timeout=COMMAND_GUARD_SECONDS)
    return result.stdout if capture else None


def main():
    version = validate_version(os.environ["PACKAGE_VERSION"])
    repository = os.environ["GITHUB_REPOSITORY"]
    tag = os.environ.get("QUALIFIED_RELEASE_TAG") or "v" + version
    if not re.fullmatch(r"v?" + re.escape(version), tag):
        raise ValueError("Qualified release tag does not match its package version")
    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    root = ROOT / "artifacts/qualified-release"
    root.mkdir(parents=True, exist_ok=False)
    feed = root / "feed"
    feed.mkdir()
    for name in [MANIFEST_NAME, "ZkLink." + version + ".nupkg", "ZkLink." + version + ".snupkg",
                 "release-evidence.json", "release-evidence.zip"]:
        gh("release", "download", tag, "--repo", repository, "--pattern", name, "--dir", str(feed))
    if (feed / "release-evidence.json").stat().st_size > MAXIMUM_INDEX_BYTES:
        raise ValueError("Release evidence index exceeds its bound")
    evidence = json.loads((feed / "release-evidence.json").read_text(encoding="utf-8"))
    run_id = str(evidence["candidate_run_id"])
    if not re.fullmatch(r"[0-9]+", run_id):
        raise ValueError("A completed candidate verification run is required")
    origin = json.loads(gh("api", f"repos/{repository}/actions/runs/{run_id}", capture=True))
    if (origin["conclusion"] != "success" or origin["head_sha"] != revision
            or origin["event"] != "workflow_dispatch"
            or origin["path"] != ".github/workflows/release-candidate.yml"
            or origin["head_repository"]["full_name"] != repository):
        raise ValueError("Candidate verification run does not qualify this source")
    digest = evidence["candidate_manifest_sha256"]
    _, candidate = read_candidate(feed, digest, revision)
    if candidate["version"] != version:
        raise ValueError("Qualified candidate version mismatch")
    # Bind release assets to the bundle actually used by the completed matrix.
    with tempfile.TemporaryDirectory(prefix="zklink-verified-candidate-") as temporary:
        original = Path(temporary)
        gh("run", "download", run_id, "--repo", repository, "--name", "zklink-candidate", "--dir", str(original))
        for name in [MANIFEST_NAME, *candidate["files"]]:
            if sha256(original / name) != sha256(feed / name):
                raise ValueError("Release assets differ from the tested candidate")
    for name in candidate["files"]:
        gh("attestation", "verify", str(feed / name), "--repo", repository,
           "--signer-workflow", repository + "/.github/workflows/release-candidate.yml")

    data = root / "evidence"
    data.mkdir()
    with zipfile.ZipFile(feed / "release-evidence.zip") as archive:
        entries = archive.infolist()
        if (len(entries) > MAXIMUM_EVIDENCE_ENTRIES
                or sum(entry.file_size for entry in entries) > MAXIMUM_EVIDENCE_BYTES):
            raise ValueError("Release evidence archive exceeds its bound")
        for entry in entries:
            target = data / entry.filename
            if (not target.resolve().is_relative_to(data.resolve())
                    or stat.S_ISLNK(entry.external_attr >> 16)
                    or entry.flag_bits & 1):
                raise ValueError("Unsafe release evidence archive entry")
        archive.extractall(data)
    # The authoritative index is the separately downloaded asset, not an index
    # supplied by the archive itself.
    (data / "release-evidence.json").write_bytes((feed / "release-evidence.json").read_bytes())
    spec = importlib.util.spec_from_file_location("release_evidence", ROOT / "dev/verify-release-evidence.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    module.verify(data, feed, digest)
    if output := os.environ.get("GITHUB_OUTPUT"):
        with open(output, "a", encoding="utf-8") as stream:
            stream.write("manifest-sha256=" + digest + "\n")
    print("Qualified immutable release assets and production evidence: PASS")


if __name__ == "__main__":
    main()
