"""Identity checks for locally packed core consumers; no Docker dependency."""

import hashlib
import json
import pathlib
import platform
import secrets

ROOT = pathlib.Path(__file__).resolve().parent.parent
CANDIDATE_NONCE_BYTES = 8


def candidate_nonce():
    return secrets.token_hex(CANDIDATE_NONCE_BYTES)


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def input_hashes():
    paths = [ROOT / name for name in (
        "Directory.Build.props", "Directory.Packages.props", "NuGet.Config", "global.json",
        "LICENSE", "THIRD-PARTY-NOTICES.md", "src/ZkLink/README.md",
        "src/ZkLink/PublicAPI.Shipped.txt", "src/ZkLink/PublicAPI.Unshipped.txt",
        "dev/core_package_artifacts.py", "dev/prepare-core-package.py",
        "dev/test-core-package.sh", "dev/test-core-package-rejections.py", "dev/release_version.py",
    )]
    for directory in ("src/ZkLink", "tests/ZkLink.PackageValidation", "tests/ZkLink.PackageConsumer"):
        paths.extend(path for path in (ROOT / directory).rglob("*")
                     if path.is_file() and path.suffix in {".cs", ".csproj", ".json"}
                     and not {"obj", "bin"}.intersection(path.relative_to(ROOT).parts))
    paths.extend((ROOT / "samples/ZkLink.Samples.Basic").glob("*.cs"))
    return {path.relative_to(ROOT).as_posix(): sha256(path) for path in sorted(set(paths))}


def read_manifest(path):
    manifest = pathlib.Path(path).resolve()
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if data["host"]["system"] != platform.system() or data["host"]["machine"] != platform.machine():
        raise ValueError("Run the installed consumer on the host that prepared it.")
    if input_hashes() != data["inputs"]:
        raise ValueError("Package inputs changed; prepare a fresh candidate.")
    for name, expected in data["artifacts"].items():
        artifact = (manifest.parent / name).resolve()
        if not artifact.is_relative_to(manifest.parent) or sha256(artifact) != expected:
            raise ValueError("Package consumer artifact identity mismatch.")
    return manifest, data
