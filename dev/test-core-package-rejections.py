#!/usr/bin/env python3
"""Check package validation rejects altered local artifacts before starting servers."""

import copy
import json
import pathlib
import subprocess
import sys
import zipfile


def main():
    inspector, run_path = sys.argv[1:]
    run = pathlib.Path(run_path)
    report = json.loads((run / "validation.json").read_text(encoding="utf-8"))
    version = report["version"]
    revision = report["revision"]
    package = run / "feed" / f"ZkLink.{version}.nupkg"
    symbols = package.with_suffix(".snupkg")
    negative = run / "rejections"
    negative.mkdir()

    def reject(name, arguments):
        result = subprocess.run(["dotnet", inspector, *map(str, arguments)], capture_output=True, check=False)
        (negative / f"{name}.txt").write_bytes(result.stdout + result.stderr)
        if result.returncode != 1 or b"Package validation failed" not in result.stderr:
            raise SystemExit(f"Rejection check failed: {name}")
        print(f"{name}: rejected", flush=True)

    def altered(name, original, transform, extra=None):
        destination = negative / f"{name}{original.suffix}"
        with zipfile.ZipFile(original) as source, zipfile.ZipFile(destination, "w") as output:
            for item in source.infolist():
                output.writestr(item.filename, transform(item.filename, source.read(item)))
            if extra:
                output.writestr(*extra)
        return destination

    def package_arguments(candidate=package, candidate_symbols=symbols, candidate_version=version):
        return ["package", candidate, candidate_symbols, candidate_version, revision, negative / "unexpected-report.json"]

    extra = altered("unexpected-asset", package, lambda _, data: data, ("runtimes/linux-x64/native/unexpected.so", b"unexpected"))
    reject("unexpected-asset", package_arguments(candidate=extra))
    dependency = altered("runtime-dependency", package, lambda name, data: data.replace(b'<group targetFramework="net10.0" />', b'<group targetFramework="net10.0"><dependency id="Unexpected" version="1.0.0" /></group>') if name.endswith(".nuspec") else data)
    reject("runtime-dependency", package_arguments(candidate=dependency))
    reject("wrong-version", package_arguments(candidate_version="0.0.0-wrong"))
    broken = altered("damaged-pdb", symbols, lambda name, data: b"BAD!" + data[4:] if name.endswith(".pdb") else data)
    reject("damaged-pdb", package_arguments(candidate_symbols=broken))
    wrong_link = altered("wrong-source-link", symbols, lambda name, data: data.replace(report["symbols"]["sourceLinkUrl"].encode(), report["symbols"]["sourceLinkUrl"].replace(revision, "0" * len(revision)).encode()) if name.endswith(".pdb") else data)
    reject("wrong-source-link", package_arguments(candidate_symbols=wrong_link))
    source_hash = bytes.fromhex(report["symbols"]["embeddedSources"][0]["sha256"])
    bad_source = altered("source-checksum", symbols, lambda name, data: data.replace(source_hash, bytes([source_hash[0] ^ 1]) + source_hash[1:], 1) if name.endswith(".pdb") else data)
    reject("source-checksum", package_arguments(candidate_symbols=bad_source))

    assets_path = run / "consumer" / "obj" / "project.assets.json"
    assets = json.loads(assets_path.read_text(encoding="utf-8"))
    source_reference = copy.deepcopy(assets)
    source_reference["libraries"][f"ZkLink/{version}"]["type"] = "project"
    altered_assets = negative / "source-reference.json"
    altered_assets.write_text(json.dumps(source_reference), encoding="utf-8")
    assembly = run / "consumer" / "bin" / "Release" / "net10.0" / "ZkLink.dll"
    reject("source-reference", ["consumer", altered_assets, assembly, run / "validation.json"])
    reject("different-assembly", ["consumer", assets_path, inspector, run / "validation.json"])


if __name__ == "__main__":
    main()
