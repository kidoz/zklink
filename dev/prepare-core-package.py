#!/usr/bin/env python3
"""Pack, inspect and execute an installed core consumer without Docker or Bash."""

import json
import os
import pathlib
import platform
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET

from core_package_artifacts import ROOT, candidate_nonce, input_hashes, sha256
from release_version import validate_version

COMMAND_GUARD_SECONDS = 300


def main():
    if len(sys.argv) > 2:
        raise SystemExit("Usage: prepare-core-package.py [package-version]")
    revision = subprocess.check_output(["git", "rev-parse", "--verify", "HEAD"], cwd=ROOT, text=True).strip()
    version = (validate_version(sys.argv[1]) if len(sys.argv) == 2
               else f"0.0.0-local.r{revision[:12]}.n{candidate_nonce()}")
    artifacts = ROOT / "artifacts/packages"
    artifacts.mkdir(parents=True, exist_ok=True)
    run = pathlib.Path(tempfile.mkdtemp(prefix="run.", dir=artifacts))
    print(f"Core package artifacts: {run}", flush=True)
    initial_inputs = input_hashes()
    environment = os.environ.copy()

    def command(name, arguments, *, env=environment, expected=0, marker=None):
        log = run / (name + ".txt")
        with log.open("w", encoding="utf-8") as output:
            result = subprocess.run(arguments, cwd=ROOT, env=env, stdout=output,
                                    stderr=subprocess.STDOUT, timeout=COMMAND_GUARD_SECONDS, check=False)
        if result.returncode != expected or (marker and marker not in log.read_text(encoding="utf-8")):
            raise SystemExit(f"{name} failed ({result.returncode}); inspect {log}")

    command("working-tree", ["git", "status", "--short"])
    (run / "revision.txt").write_text(revision + "\n", encoding="utf-8")
    (run / "version.txt").write_text(version + "\n", encoding="utf-8")
    command("dotnet-info", ["dotnet", "--info"])
    for path in (ROOT / "src/ZkLink").glob("PublicAPI.*.txt"):
        shutil.copy2(path, run / path.name)
    feed = run / "feed"
    feed.mkdir()
    command("restore", ["dotnet", "restore", "src/ZkLink", "--locked-mode"])
    command("pack", ["dotnet", "pack", "src/ZkLink", "-c", "Release", "--no-restore",
                     f"-p:Version={version}", "-p:ContinuousIntegrationBuild=true", "-o", str(feed)])
    command("inspector-restore", ["dotnet", "restore", "tests/ZkLink.PackageValidation", "--locked-mode"])
    command("inspector-build", ["dotnet", "build", "tests/ZkLink.PackageValidation", "-c", "Release", "--no-restore"])
    inspector = ROOT / "tests/ZkLink.PackageValidation/bin/Release/net10.0/ZkLink.PackageValidation.dll"
    package = feed / f"ZkLink.{version}.nupkg"
    symbols = package.with_suffix(".snupkg")
    validation = run / "validation.json"
    command("validation", ["dotnet", str(inspector), "package", str(package), str(symbols),
                           version, revision, str(validation)], marker="Package validation: PASS")
    report = json.loads(validation.read_text(encoding="utf-8"))

    consumer = run / "consumer"
    samples = consumer / "Samples"
    samples.mkdir(parents=True)
    for source in (ROOT / "tests/ZkLink.PackageConsumer").iterdir():
        if source.is_file() and source.suffix in {".cs", ".csproj"}:
            shutil.copy2(source, consumer / source.name)
    for source in (ROOT / "samples/ZkLink.Samples.Basic").glob("*.cs"):
        if source.name != "Program.cs":
            shutil.copy2(source, samples / source.name)
    (consumer / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    audit = ET.SubElement(config, "auditSources")
    ET.SubElement(audit, "clear")
    ET.SubElement(audit, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    config_path = consumer / "NuGet.Config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    environment["NUGET_PACKAGES"] = str(run / "package-cache")
    properties = [f"-p:ZkLinkPackageVersion={version}"]
    for name, mode in (("consumer-restore", "--force-evaluate"), ("consumer-locked-restore", "--locked-mode")):
        command(name, ["dotnet", "restore", str(consumer), "--configfile", str(config_path), mode, *properties])
    command("consumer-build", ["dotnet", "build", str(consumer), "-c", "Release", *properties, "--no-restore"])
    binary = consumer / "bin/Release/net10.0/ZkLink.PackageConsumer.dll"
    assembly = binary.with_name("ZkLink.dll")
    command("consumer-assets", ["dotnet", str(inspector), "consumer", str(consumer / "obj/project.assets.json"),
                               str(assembly), str(validation)], marker="Package validation: PASS")
    command("rejections", [sys.executable, str(ROOT / "dev/test-core-package-rejections.py"), str(inspector), str(run)])
    environment["ZKLINK_PACKAGE_ASSEMBLY_SHA256"] = report["assemblySha256"]
    command("consumer-identity", ["dotnet", str(binary), "--package-check"], marker="Package identity check: PASS")
    wrong_environment = environment | {"ZKLINK_PACKAGE_ASSEMBLY_SHA256": "incorrect"}
    command("wrong-loaded-assembly", ["dotnet", str(binary), "--package-check"], env=wrong_environment,
            expected=1, marker="The loaded driver does not match the candidate package.")
    if input_hashes() != initial_inputs or subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip() != revision:
        raise SystemExit("Source inputs changed during verification; prepare a fresh candidate.")
    files = [package, symbols, validation, binary, assembly,
             binary.with_suffix(".deps.json"), binary.with_suffix(".runtimeconfig.json"),
             consumer / "obj/packages.lock.json", consumer / "obj/project.assets.json", config_path]
    receipt = {"revision": revision, "version": version,
               "host": {"system": platform.system(), "machine": platform.machine(), "os": platform.platform(), "python": sys.version},
               "consumer": binary.relative_to(run).as_posix(), "assemblySha256": report["assemblySha256"],
               "inputs": initial_inputs, "artifacts": {path.relative_to(run).as_posix(): sha256(path) for path in files}}
    manifest = run / "package-consumer.json"
    manifest.write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    (run / "inspection-result.txt").write_text("PASS (package, symbols, installed consumer identity and rejection checks; no live servers)\n", encoding="utf-8")
    print(f"Core package inspection: PASS. Manifest: {manifest}", flush=True)


if __name__ == "__main__":
    main()
