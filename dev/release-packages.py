#!/usr/bin/env python3
"""Verify the three coordinated packages and their installed consumers."""
import argparse
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile

from release_candidate import ROOT, read_candidate, sha256
from release_version import validate_version

PACKAGES = {"ZkLink": True, "ZkLink.Extensions.DependencyInjection": True, "ZkLink.Analyzers": False}
MANIFEST = "release-packages.json"
PUBLIC_SOURCE = "https://api.nuget.org/v3/index.json"
REPOSITORY = "https://github.com/kidoz/zklink"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def filenames(version):
    return [name + "." + version + suffix for name, symbols in PACKAGES.items()
            for suffix in ([".nupkg", ".snupkg"] if symbols else [".nupkg"])]


def inspect_package(path, package_id, version, revision, *, symbols=False):
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        require(len(names) == len(set(names)), "Duplicate package member")
        require(all(entry.file_size <= 16 * 1024 * 1024 for entry in archive.infolist()), "Package member too large")
        metadata = ET.fromstring(archive.read(package_id + ".nuspec")).find("{*}metadata")
        require(metadata.findtext("{*}id") == package_id and metadata.findtext("{*}version") == version,
                "Package ID or version mismatch")
        repository = metadata.find("{*}repository")
        require(repository is not None and repository.get("url") == REPOSITORY
                and repository.get("commit") == revision, "Package source mismatch")
        if symbols:
            require("lib/net10.0/" + package_id + ".pdb" in names, "Missing portable symbols")
            return
        require(metadata.findtext("{*}license") == "MIT", "Missing MIT license metadata")
        require({"README.md", "LICENSE", "THIRD-PARTY-NOTICES.md"}.issubset(names), "Missing package documentation/notices")
        dependencies = {item.get("id"): item.get("version") for item in metadata.findall(".//{*}dependency")}
        if package_id == "ZkLink.Extensions.DependencyInjection":
            require(set(dependencies) == {"ZkLink", "Microsoft.Extensions.DependencyInjection.Abstractions",
                                          "Microsoft.Extensions.Hosting.Abstractions"}, "Unexpected DI dependencies")
            require(dependencies["ZkLink"].strip("[](), ").split(",")[0].strip() == version,
                    "DI package requires a different core version")
        else:
            require(not dependencies, "Core and analyzers must have no runtime package dependencies")
        if package_id == "ZkLink.Analyzers":
            require("analyzers/dotnet/cs/ZkLink.Analyzers.dll" in names
                    and not any(name.startswith(("lib/", "runtimes/")) for name in names), "Invalid analyzer assets")
        else:
            require({"lib/net10.0/" + package_id + suffix for suffix in [".dll", ".xml"]}.issubset(names),
                    "Missing runtime assembly or XML documentation")


def check_packages(feed, version):
    version = validate_version(version)
    require({path.name for path in feed.glob("*.nupkg")} | {path.name for path in feed.glob("*.snupkg")}
            == set(filenames(version)), "Expected exactly three packages and two symbol packages")
    digest = sha256(feed / "candidate.json")
    _, candidate = read_candidate(feed, digest)
    require(candidate["version"] == version, "Core candidate version differs from release")
    for name in filenames(version):
        path = feed / name
        require(not path.is_symlink() and path.is_file() and path.resolve().parent == feed,
                "Unsafe release package path")
    for package_id, symbols in PACKAGES.items():
        inspect_package(feed / (package_id + "." + version + ".nupkg"), package_id, version, candidate["revision"])
        if symbols:
            inspect_package(feed / (package_id + "." + version + ".snupkg"), package_id, version,
                            candidate["revision"], symbols=True)
    return {"schema_version": 1, "version": version, "revision": candidate["revision"],
            "candidate_manifest_sha256": digest, "files": {name: sha256(feed / name) for name in filenames(version)}}


def verify_manifest(feed, digest):
    path = feed / MANIFEST
    require(not path.is_symlink() and sha256(path) == digest.upper(), "Release manifest identity mismatch")
    recorded = json.loads(path.read_text())
    require(check_packages(feed, recorded["version"]) == recorded, "Release package identity mismatch")
    return recorded


def command(run, name, arguments, *, environment=None, expected=0):
    log = run / (name + ".log")
    with log.open("w") as output:
        result = subprocess.run(arguments, cwd=ROOT, env=environment, text=True, stdout=output,
                                stderr=subprocess.STDOUT, timeout=180)
    require(result.returncode == expected, name + " failed; inspect " + str(log))
    return log.read_text()


def consumer(feed, version, run, *, public=False):
    project = run / "consumer"
    shutil.copytree(ROOT / "tests/ZkLink.ReleaseConsumer", project,
                    ignore=shutil.ignore_patterns("bin", "obj"))
    (project / "Directory.Build.props").write_text("<Project />\n")
    extensions_version = ET.parse(ROOT / "Directory.Packages.props").find(
        ".//PackageVersion[@Include='Microsoft.Extensions.Hosting']").get("Version")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    if not public:
        ET.SubElement(sources, "add", key="release", value=str(feed))
    ET.SubElement(sources, "add", key="public", value=PUBLIC_SOURCE)
    config_path = project / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    cache = run / "package-cache"
    environment = os.environ | {"NUGET_PACKAGES": str(cache)}
    properties = ["-p:ZkLinkPackageVersion=" + version, "-p:MicrosoftExtensionsVersion=" + extensions_version]
    command(run, "consumer-restore", ["dotnet", "restore", str(project), "--configfile", str(config_path),
                                      "--force-evaluate", *properties], environment=environment)
    command(run, "consumer-locked-restore", ["dotnet", "restore", str(project), "--configfile", str(config_path),
                                             "--locked-mode", *properties], environment=environment)
    for package_id in PACKAGES:
        installed = cache / package_id.lower() / version
        origin = json.loads((installed / ".nupkg.metadata").read_text())["source"]
        require(origin.rstrip("/") == PUBLIC_SOURCE if public else Path(origin).resolve() == feed,
                "Consumer resolved a package from the wrong feed")
        with zipfile.ZipFile(feed / (package_id + "." + version + ".nupkg")) as original:
            with zipfile.ZipFile(installed / (package_id.lower() + "." + version + ".nupkg")) as actual:
                for entry in original.infolist():
                    if not entry.is_dir() and entry.filename != ".signature.p7s":
                        require(actual.read(entry.filename) == original.read(entry.filename), "Installed package payload differs")
            if package_id != "ZkLink.Analyzers":
                variable = "ZKLINK_EXPECTED_CORE_SHA256" if package_id == "ZkLink" else "ZKLINK_EXPECTED_DI_SHA256"
                environment[variable] = hashlib.sha256(original.read("lib/net10.0/" + package_id + ".dll")).hexdigest().upper()
    clean = command(run, "consumer-build", ["dotnet", "build", str(project), "-c", "Release", "--no-restore", *properties],
                    environment=environment)
    require("ZKL000" not in clean, "Clean package consumer reported analyzer diagnostics")
    binary = project / "bin/Release/net10.0/ZkLink.ReleaseConsumer.dll"
    output = command(run, "consumer-run", ["dotnet", str(binary)], environment=environment)
    require("Release package identities and DI registration: PASS" in output, "Package consumer did not pass")
    diagnostic_log = run / "consumer-diagnostic.log"
    with diagnostic_log.open("w") as output:
        result = subprocess.run(["dotnet", "build", str(project), "-c", "Release", "--no-restore", *properties,
                                 "-p:DefineConstants=EXPECT_ANALYZER_DIAGNOSTIC"], cwd=ROOT, env=environment,
                                stdout=output, stderr=subprocess.STDOUT, timeout=180)
    require(result.returncode != 0 and "error ZKL0004" in diagnostic_log.read_text(),
            "Installed analyzer did not reject the intentional unobserved call")


def symbol_reports(feed, version, run):
    command(run, "inspector-build", ["dotnet", "build", "tests/ZkLink.PackageValidation", "-c", "Release"])
    inspector = ROOT / "tests/ZkLink.PackageValidation/bin/Release/net10.0/ZkLink.PackageValidation.dll"
    revision = json.loads((feed / MANIFEST).read_text())["revision"]
    reports = {}
    for package_id, symbols in PACKAGES.items():
        if symbols:
            report = run / (package_id + "-symbols.json")
            command(run, package_id + "-symbols", ["dotnet", str(inspector), "symbols",
                    str(feed / (package_id + "." + version + ".nupkg")),
                    str(feed / (package_id + "." + version + ".snupkg")), package_id, revision, str(report)])
            reports[package_id] = json.loads(report.read_text())
    return reports


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("mode", choices=["manifest", "verify", "consumer", "public"])
    parser.add_argument("feed", type=Path)
    parser.add_argument("--version")
    parser.add_argument("--sha256")
    args = parser.parse_args()
    feed = args.feed.resolve()
    if args.mode == "manifest":
        record = check_packages(feed, args.version)
        (feed / MANIFEST).write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
        digest = sha256(feed / MANIFEST)
        if output := os.environ.get("GITHUB_OUTPUT"):
            with open(output, "a") as stream:
                stream.write("release-manifest-sha256=" + digest + "\n")
        print("Release manifest SHA256: " + digest)
        return
    require(bool(args.sha256), "A release manifest SHA256 is required")
    record = verify_manifest(feed, args.sha256)
    if args.mode == "verify":
        print("All three release packages and symbols: PASS")
        return
    parent = ROOT / "artifacts/release-consumers"
    parent.mkdir(parents=True, exist_ok=True)
    run = Path(tempfile.mkdtemp(prefix=args.mode + ".", dir=parent))
    print("Release consumer evidence: " + str(run), flush=True)
    reports = symbol_reports(feed, record["version"], run)
    if args.mode == "public":
        spec = importlib.util.spec_from_file_location("published_core", ROOT / "dev/verify-published-core.py")
        public = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(public)
        for package_id in PACKAGES:
            lower = package_id.lower()
            path = run / (package_id + "." + record["version"] + ".nupkg")
            path.write_bytes(public.download("https://api.nuget.org/v3-flatcontainer/" + lower + "/" + record["version"]
                                            + "/" + lower + "." + record["version"] + ".nupkg"))
        for package_id, report in reports.items():
            pdb = public.download(public.SYMBOL_SOURCE + "/" + package_id + ".pdb/" + report["symbolServerKey"] + "/" + package_id + ".pdb",
                  headers={"SymbolChecksumValidationSupported": "1", "SymbolChecksum": "SHA256:" + report["pdbChecksumSha256"]})
            with zipfile.ZipFile(feed / (package_id + "." + record["version"] + ".snupkg")) as symbols:
                require(pdb == symbols.read("lib/net10.0/" + package_id + ".pdb"), "Indexed symbols differ from release")
    consumer(feed, record["version"], run, public=args.mode == "public")
    (run / "result.json").write_text(json.dumps({"version": record["version"], "passed": True,
        "packages": list(PACKAGES), "public_feed_verified": args.mode == "public",
        "indexed_symbols_verified": list(reports) if args.mode == "public" else []}, indent=2) + "\n")
    print("All three installed release packages and analyzer diagnostic: PASS")


if __name__ == "__main__":
    main()
