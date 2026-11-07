"""Package metadata and public-feed inputs for native core/DI verification."""

import hashlib
import importlib.util
import re
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from aot_artifacts import ROOT, sha256
from release_version import validate_version

DI_ID = "ZkLink.Extensions.DependencyInjection"
PUBLIC_FEED = "https://api.nuget.org/v3-flatcontainer"
MAXIMUM_PACKAGE_BYTES = 32 * 1024 * 1024

_spec = importlib.util.spec_from_file_location("release_packages", ROOT / "dev/release-packages.py")
_packages = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_packages)


def assembly_hash(package, package_id):
    with zipfile.ZipFile(package) as archive:
        return hashlib.sha256(archive.read(f"lib/net10.0/{package_id}.dll")).hexdigest().upper()


def inspect_di(feed, version, revision, *, symbols=True):
    package = feed / f"{DI_ID}.{version}.nupkg"
    _packages.inspect_package(package, DI_ID, version, revision)
    symbol_package = package.with_suffix(".snupkg")
    if symbols:
        _packages.inspect_package(symbol_package, DI_ID, version, revision, symbols=True)
    return {"packageSha256": sha256(package),
            "symbolsSha256": sha256(symbol_package) if symbols else None,
            "assemblySha256": assembly_hash(package, DI_ID)}


def public_packages(feed, version):
    version = validate_version(version)
    for package_id in ["ZkLink", DI_ID]:
        lower = package_id.lower()
        url = f"{PUBLIC_FEED}/{lower}/{version.lower()}/{lower}.{version.lower()}.nupkg"
        with urllib.request.urlopen(url, timeout=30) as response:
            data = response.read(MAXIMUM_PACKAGE_BYTES + 1)
        if len(data) > MAXIMUM_PACKAGE_BYTES:
            raise ValueError("Published native package exceeds its size bound")
        (feed / f"{package_id}.{version}.nupkg").write_bytes(data)
    core = feed / f"ZkLink.{version}.nupkg"
    with zipfile.ZipFile(core) as archive:
        metadata = ET.fromstring(archive.read("ZkLink.nuspec")).find("{*}metadata")
        revision = metadata.find("{*}repository").get("commit")
    if not revision or not re.fullmatch(r"[0-9a-f]{40}", revision):
        raise ValueError("Published native package has no full source revision")
    _packages.inspect_package(core, "ZkLink", version, revision)
    report = {"packageId": "ZkLink", "version": version, "revision": revision,
              "origin": PUBLIC_FEED, "packageSha256": sha256(core),
              "assemblySha256": assembly_hash(core, "ZkLink")}
    return report, inspect_di(feed, version, revision, symbols=False)
