#!/usr/bin/env python3
"""Pack, inspect and natively publish two independent consumers on the current host."""

import json
import os
import pathlib
import platform
import re
import shutil
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET
from aot_artifacts import ROOT, RIDS, native_header, sha256
from release_version import local_candidate_version

def input_hashes():
    inputs = [ROOT / "src/ZkLink/ZkLink.csproj", ROOT / "src/ZkLink/packages.lock.json", *sorted((ROOT / "src/ZkLink").rglob("*.cs"))]
    inputs += [ROOT / name for name in ["Directory.Build.props", "Directory.Packages.props", "NuGet.Config", "global.json", "LICENSE", "THIRD-PARTY-NOTICES.md", "src/ZkLink/README.md", "src/ZkLink/PublicAPI.Shipped.txt", "src/ZkLink/PublicAPI.Unshipped.txt"]]
    inputs += list((ROOT / "tests/ZkLink.AotAnalysis").glob("*.cs*")) + list((ROOT / "tests/ZkLink.AotSmoke").glob("*.cs*"))
    inputs += list((ROOT / "samples/ZkLink.Samples.Basic").glob("*.cs"))
    inputs += [ROOT / "tests/ZkLink.IntegrationTests/WatchReconnectProxy.cs", ROOT / "dev/aot_artifacts.py", pathlib.Path(__file__)]
    inputs += [ROOT / "dev/release_version.py"]
    return {str(path.relative_to(ROOT)): sha256(path) for path in inputs if not {"obj", "bin"}.intersection(path.relative_to(ROOT).parts)}


def main():
    host = RIDS.get((platform.system(), platform.machine()))
    rid = sys.argv[1] if len(sys.argv) == 2 else host
    if len(sys.argv) > 2 or not rid or rid != host:
        raise SystemExit("Use the matching linux-x64, linux-arm64, win-x64 or osx-arm64 host; cross publication is not qualification.")
    artifacts = ROOT / "artifacts" / "aot"
    artifacts.mkdir(parents=True, exist_ok=True)
    run = pathlib.Path(tempfile.mkdtemp(prefix="run.", dir=artifacts))
    print(f"Native AOT artifacts: {run}", flush=True)
    initial_inputs = input_hashes()
    feed = run / "feed"
    feed.mkdir()
    environment = os.environ.copy()

    def command(name, arguments, *, cwd=ROOT, env=environment):
        with (run / f"{name}.txt").open("w", encoding="utf-8") as output:
            result = subprocess.run(arguments, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT, check=False)
        if result.returncode:
            raise SystemExit(f"{name} failed ({result.returncode}); inspect {run / (name + '.txt')}")

    revision = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()
    version = local_candidate_version(revision)
    command("sdk", ["dotnet", "--info"])
    command("working-tree", ["git", "status", "--short"])
    (run / "host.json").write_text(json.dumps({"rid": rid, "os": platform.platform(), "machine": platform.machine(), "libc": platform.libc_ver(), "python": sys.version, "revision": revision}, indent=2) + "\n", encoding="utf-8")
    compiler = shutil.which("clang") or shutil.which("cl")
    if compiler:
        with (run / "native-toolchain.txt").open("w", encoding="utf-8") as output:
            subprocess.run([compiler, "--version"] if pathlib.Path(compiler).stem != "cl" else [compiler], stdout=output, stderr=subprocess.STDOUT, check=False)
    command("core-restore", ["dotnet", "restore", "src/ZkLink", "--locked-mode"])
    command("pack", ["dotnet", "pack", "src/ZkLink", "-c", "Release", "--no-restore", f"-p:Version={version}", "-p:ContinuousIntegrationBuild=true", "-o", str(feed)])
    command("inspector-restore", ["dotnet", "restore", "tests/ZkLink.PackageValidation", "--locked-mode"])
    command("inspector-build", ["dotnet", "build", "tests/ZkLink.PackageValidation", "-c", "Release", "--no-restore"])
    inspector = ROOT / "tests/ZkLink.PackageValidation/bin/Release/net10.0/ZkLink.PackageValidation.dll"
    package = feed / f"ZkLink.{version}.nupkg"
    command("package-validation", ["dotnet", str(inspector), "package", str(package), str(package.with_suffix(".snupkg")), version, revision, str(run / "package.json")])
    package_report = json.loads((run / "package.json").read_text(encoding="utf-8"))

    config = ET.Element("configuration")
    sources = ET.SubElement(config, "packageSources")
    ET.SubElement(sources, "clear")
    ET.SubElement(sources, "add", key="candidate", value=str(feed))
    ET.SubElement(sources, "add", key="nuget.org", value="https://api.nuget.org/v3/index.json")
    mapping = ET.SubElement(config, "packageSourceMapping")
    candidate = ET.SubElement(mapping, "packageSource", key="candidate")
    ET.SubElement(candidate, "package", pattern="ZkLink")
    sdk = ET.SubElement(mapping, "packageSource", key="nuget.org")
    for pattern in ["Microsoft.*", "runtime.*", "System.*"]:
        ET.SubElement(sdk, "package", pattern=pattern)
    config_path = run / "NuGet.config"
    ET.ElementTree(config).write(config_path, encoding="utf-8", xml_declaration=True)
    (run / "Directory.Build.props").write_text("<Project />\n", encoding="utf-8")
    environment["NUGET_PACKAGES"] = str(run / "package-cache")
    receipt = {"rid": rid, "revision": revision, "version": version, "packageSha256": package_report["packageSha256"], "consumers": {}}
    for name in ["ZkLink.AotAnalysis", "ZkLink.AotSmoke"]:
        project = run / name
        project.mkdir()
        for source in (ROOT / "tests" / name).iterdir():
            if source.is_file() and source.suffix in {".cs", ".csproj"}:
                shutil.copy2(source, project / source.name)
        if name == "ZkLink.AotSmoke":
            examples = project / "Examples"
            examples.mkdir()
            for source in (ROOT / "samples/ZkLink.Samples.Basic").glob("*.cs"):
                if source.name in {"NodeSample.cs", "TransactionSample.cs", "AuxiliarySample.cs", "WatchSample.cs", "SampleNodes.cs", "ISampleStep.cs"}:
                    shutil.copy2(source, examples / source.name)
            shutil.copy2(ROOT / "tests/ZkLink.IntegrationTests/WatchReconnectProxy.cs", project / "WatchReconnectProxy.cs")
            tree = ET.parse(project / f"{name}.csproj")
            if tree.findall(".//TrimmerRootAssembly") or tree.findall(".//TrimmerRootDescriptor") or tree.findall(".//ProjectReference"):
                raise SystemExit("The representative consumer must not root the core or reference its source project.")
        properties = [f"-p:ZkLinkPackageVersion={version}", f"-p:RuntimeIdentifier={rid}"]
        command(name + "-restore", ["dotnet", "restore", str(project), "--configfile", str(config_path), "--force-evaluate", *properties], cwd=project)
        command(name + "-locked-restore", ["dotnet", "restore", str(project), "--configfile", str(config_path), "--locked-mode", *properties], cwd=project)
        assets = json.loads((project / "obj/project.assets.json").read_text(encoding="utf-8"))
        library = assets["libraries"][f"ZkLink/{version}"]
        if library["type"] != "package" or any(value["type"] == "project" for value in assets["libraries"].values()):
            raise SystemExit("Native consumer resolved a source project.")
        installed = run / "package-cache" / library["path"] / "lib/net10.0/ZkLink.dll"
        if sha256(installed) != package_report["assemblySha256"]:
            raise SystemExit("Native compiler input differs from the candidate.")
        output = run / (name + "-publish")
        command(name + "-publish", ["dotnet", "publish", str(project), "-c", "Release", "--no-restore", *properties, "-o", str(output)], cwd=project)
        log = (run / (name + "-publish.txt")).read_text(encoding="utf-8")
        if re.search(r"\bwarning\s+IL\d+", log, re.IGNORECASE):
            raise SystemExit("Unreviewed trim/AOT publish warning.")
        binary = output / (name + (".exe" if rid == "win-x64" else ""))
        if not native_header(binary, rid) or (output / "ZkLink.dll").exists():
            raise SystemExit("Expected a native executable with the correct architecture.")
        dependency_tool = shutil.which("otool" if rid == "osx-arm64" else "ldd" if rid.startswith("linux-") else "dumpbin")
        if dependency_tool:
            switches = ["-L"] if rid == "osx-arm64" else [] if rid.startswith("linux-") else ["/dependents"]
            command(name + "-native-dependencies", [dependency_tool, *switches, str(binary)])
        notices = output / "notices"
        notices.mkdir()
        for source in [ROOT / "LICENSE", ROOT / "THIRD-PARTY-NOTICES.md"]:
            shutil.copy2(source, notices / source.name)
        runtime_packages = list((run / "package-cache" / f"microsoft.netcore.app.runtime.nativeaot.{rid}").glob("*"))
        if len(runtime_packages) != 1:
            raise SystemExit("Expected one SDK-selected Native AOT runtime pack.")
        for notice_name in ["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"]:
            shutil.copy2(runtime_packages[0] / notice_name, notices / ("dotnet-" + notice_name))
        (notices / "dotnet-runtime-pack.txt").write_text(runtime_packages[0].parent.name + "/" + runtime_packages[0].name + "\n", encoding="utf-8")
        receipt["consumers"][name] = {"executable": str(binary.relative_to(run)), "sha256": sha256(binary), "nativeHeaderVerified": True, "candidateCompilerInputVerified": True}
        if name == "ZkLink.AotAnalysis":
            command("analysis-direct-execution", [str(binary)], cwd=output)
        else:
            environment["ZKLINK_AOT_EXPECTED_RID"] = rid
            environment["ZKLINK_AOT_EXECUTABLE_SHA256"] = sha256(binary)
            command("smoke-native-identity", [str(binary), "--native-check"], cwd=output)
            with (run / "smoke-managed-rejection.txt").open("w", encoding="utf-8") as log:
                managed = subprocess.run(["dotnet", str(project / f"bin/Release/net10.0/{rid}/{name}.dll"), "--native-check"], env=environment, stdout=log, stderr=subprocess.STDOUT, check=False)
            if managed.returncode != 1 or "Native smoke failed: InvalidOperationException" not in (run / "smoke-managed-rejection.txt").read_text(encoding="utf-8"):
                raise SystemExit("The native assertion did not reject managed execution.")
    if input_hashes() != initial_inputs:
        raise SystemExit("Source inputs changed during publication; publish a fresh candidate.")
    receipt["inputs"] = initial_inputs
    (run / "native.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8")
    print(f"Native publish and rooted execution: PASS ({rid}). Live smoke remains required. Manifest: {run / 'native.json'}", flush=True)


if __name__ == "__main__":
    main()
