#!/usr/bin/env python3
"""Verify public package contents, fresh public-only consumption and indexed symbols."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

from core_package_artifacts import ROOT, sha256
from release_version import validate_version

PUBLIC_SOURCE = "https://api.nuget.org/v3/index.json"
SYMBOL_SOURCE = "https://symbols.nuget.org/download/symbols"
COMMAND_GUARD_SECONDS = 120
INDEXING_GUARD_SECONDS = 600
HTTP_GUARD_SECONDS = 20
POLL_SECONDS = 10
MAXIMUM_DOWNLOAD_BYTES = 64 * 1024 * 1024
ASSEMBLY_ENTRY = "lib/net10.0/ZkLink.dll"
PDB_ENTRY = "lib/net10.0/ZkLink.pdb"


def download(url, *, headers=None):
    deadline = time.monotonic() + INDEXING_GUARD_SECONDS
    while True:
        try:
            request = urllib.request.Request(url, headers=headers or {})
            with urllib.request.urlopen(request, timeout=HTTP_GUARD_SECONDS) as response:
                content = response.read(MAXIMUM_DOWNLOAD_BYTES + 1)
            if len(content) > MAXIMUM_DOWNLOAD_BYTES:
                raise ValueError("Published artifact exceeds its download bound")
            return content
        except urllib.error.HTTPError as error:
            if error.code not in {404, 408, 429, 500, 502, 503, 504} or time.monotonic() >= deadline:
                raise
        if time.monotonic() >= deadline:
            raise TimeoutError("Public indexing verification exceeded its guard")
        time.sleep(POLL_SECONDS)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--version", required=True)
    parser.add_argument("--package", required=True, type=Path)
    parser.add_argument("--symbols", required=True, type=Path)
    args = parser.parse_args()
    version = validate_version(args.version)
    root = ROOT / "artifacts/published"
    root.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix="run.", dir=root))
    print("Public consumer evidence: " + str(run), flush=True)
    with zipfile.ZipFile(args.package) as archive:
        metadata = ET.fromstring(archive.read("ZkLink.nuspec")).find("{*}metadata")
        if metadata.findtext("{*}version") != version:
            raise ValueError("Public verification version mismatch")
        revision = metadata.find("{*}repository").attrib["commit"]
        assembly_hash = hashlib.sha256(archive.read(ASSEMBLY_ENTRY)).hexdigest().upper()

    def command(name, arguments, environment=None):
        with (run / (name + ".log")).open("w") as output:
            subprocess.run(arguments, cwd=ROOT, env=environment, stdout=output,
                           stderr=subprocess.STDOUT, check=True, timeout=COMMAND_GUARD_SECONDS)

    command("inspector-restore", ["dotnet", "restore", "tests/ZkLink.PackageValidation", "--locked-mode"])
    command("inspector-build", ["dotnet", "build", "tests/ZkLink.PackageValidation", "-c", "Release", "--no-restore"])
    inspector = ROOT / "tests/ZkLink.PackageValidation/bin/Release/net10.0/ZkLink.PackageValidation.dll"
    report = run / "expected.json"
    command("expected-package", ["dotnet", str(inspector), "package", str(args.package.resolve()), str(args.symbols.resolve()), version, revision, str(report)])
    expected = json.loads(report.read_text())
    published = run / ("ZkLink." + version + ".nupkg")
    published.write_bytes(download("https://api.nuget.org/v3-flatcontainer/zklink/" + version + "/zklink." + version + ".nupkg"))
    # Repository signing changes the archive identity. Compare every original
    # payload entry, including the assembly, documentation and package metadata.
    with zipfile.ZipFile(args.package) as original, zipfile.ZipFile(published) as public:
        for entry in original.infolist():
            if not entry.is_dir() and entry.filename != ".signature.p7s":
                if public.read(entry.filename) != original.read(entry.filename):
                    raise ValueError("Published package payload differs from the qualified candidate")

    consumer = run / "consumer"
    consumer.mkdir()
    for source in (ROOT / "tests/ZkLink.PackageConsumer").iterdir():
        if source.suffix in {".cs", ".csproj"}:
            shutil.copy2(source, consumer / source.name)
    samples = consumer / "Samples"
    samples.mkdir()
    for source in (ROOT / "samples/ZkLink.Samples.Basic").glob("*.cs"):
        if source.name != "Program.cs":
            shutil.copy2(source, samples / source.name)
    (consumer / "Directory.Build.props").write_text("<Project />\n")
    config = consumer / "NuGet.Config"
    config.write_text('<configuration><packageSources><clear/><add key="public" value="' + PUBLIC_SOURCE + '"/></packageSources></configuration>\n')
    environment = dict(os.environ, NUGET_PACKAGES=str(run / "package-cache"), ZKLINK_PACKAGE_ASSEMBLY_SHA256=assembly_hash)
    command("public-only-restore", ["dotnet", "restore", str(consumer), "--configfile", str(config), "--no-cache", "-p:ZkLinkPackageVersion=" + version], environment)
    origin = json.loads((run / "package-cache/zklink" / version / ".nupkg.metadata").read_text())
    if origin["source"].rstrip("/") != PUBLIC_SOURCE:
        raise ValueError("Fresh consumer did not use the public package feed")
    command("public-consumer-build", ["dotnet", "build", str(consumer), "-c", "Release", "--no-restore", "-p:ZkLinkPackageVersion=" + version], environment)
    binary = consumer / "bin/Release/net10.0/ZkLink.PackageConsumer.dll"
    command("public-consumer-identity", ["dotnet", str(binary), "--package-check"], environment)
    pdb = download(SYMBOL_SOURCE + "/ZkLink.pdb/" + expected["symbols"]["symbolServerKey"] + "/ZkLink.pdb",
                   headers={"SymbolChecksumValidationSupported": "1", "SymbolChecksum": "SHA256:" + expected["symbols"]["pdbChecksumSha256"]})
    with zipfile.ZipFile(args.symbols) as symbols:
        if pdb != symbols.read(PDB_ENTRY):
            raise ValueError("Public symbols differ from the qualified symbols")
    (run / "ZkLink.pdb").write_bytes(pdb)
    (run / "public-result.json").write_text(json.dumps({"version": version, "passed": True,
        "published_package_sha256": sha256(published), "assembly_sha256": assembly_hash,
        "public_feed_verified": True, "public_symbols_sha256": hashlib.sha256(pdb).hexdigest().upper()}, indent=2) + "\n")
    print("Public package consumer and indexed symbols: PASS")


if __name__ == "__main__":
    main()
