"""Shared local AOT artifact identity checks; no service or driver runtime dependency."""

import hashlib
import json
import pathlib
import platform
import struct

ROOT = pathlib.Path(__file__).resolve().parent.parent
RIDS = {("Linux", "x86_64"): "linux-x64", ("Linux", "aarch64"): "linux-arm64",
        ("Darwin", "arm64"): "osx-arm64", ("Windows", "AMD64"): "win-x64"}


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest().upper()


def native_header(path, rid):
    with path.open("rb") as stream:
        header = stream.read(64)
        if rid.startswith("linux-"):
            expected_machine = 183 if rid == "linux-arm64" else 62
            return header[:6] == b"\x7fELF\x02\x01" and struct.unpack_from("<H", header, 18)[0] == expected_machine
        if rid == "osx-arm64":
            return struct.unpack_from("<II", header) == (0xFEEDFACF, 0x0100000C)
        if header[:2] == b"MZ":
            stream.seek(struct.unpack_from("<I", header, 60)[0])
            return stream.read(6) == b"PE\x00\x00\x64\x86"
        return False


def read_manifest(path):
    manifest = pathlib.Path(path).resolve()
    data = json.loads(manifest.read_text(encoding="utf-8"))
    if data["rid"] != RIDS.get((platform.system(), platform.machine())):
        raise ValueError("The native artifact must run on its own OS and architecture.")
    for name, expected in data["inputs"].items():
        source = (ROOT / name).resolve()
        if not source.is_relative_to(ROOT) or sha256(source) != expected:
            raise ValueError("Native input changed; publish a fresh candidate.")
    for consumer in data["consumers"].values():
        binary = (manifest.parent / consumer["executable"]).resolve()
        if not binary.is_relative_to(manifest.parent) or sha256(binary) != consumer["sha256"] or not native_header(binary, data["rid"]):
            raise ValueError("Native executable identity mismatch.")
    package = manifest.parent / "feed" / f"ZkLink.{data['version']}.nupkg"
    if sha256(package) != data["packageSha256"]:
        raise ValueError("Native candidate package identity mismatch.")
    return manifest, data
