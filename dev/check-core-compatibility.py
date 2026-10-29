#!/usr/bin/env python3
"""Compare a packed core candidate with its published baseline using the pinned SDK."""

import hashlib
import json
import pathlib
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET
import zipfile

from core_package_artifacts import ROOT, sha256

COMMAND_GUARD_SECONDS = 120
ASSEMBLY_ENTRY = "lib/net10.0/ZkLink.dll"
PUBLIC_SOURCE = "https://api.nuget.org/v3/index.json"


def main():
    if len(sys.argv) != 2:
        raise SystemExit("Usage: check-core-compatibility.py <inspected-package-run>")
    run = pathlib.Path(sys.argv[1]).resolve()
    artifacts = run / "compatibility"
    artifacts.mkdir()
    properties = ["MSBuildSDKsPath", "MSBuildToolsPath", "NuGetPackageRoot", "PackageId",
                  "PackageValidationBaselineVersion", "EnablePackageValidation",
                  "ApiCompatEnableRuleCannotChangeParameterName", "ApiCompatEnableRuleAttributesMustMatch"]
    result = subprocess.run(["dotnet", "msbuild", str(ROOT / "src/ZkLink"), "-nologo", "-p:Configuration=Release",
                             "-getProperty:" + ",".join(properties)], cwd=ROOT, capture_output=True,
                            text=True, timeout=COMMAND_GUARD_SECONDS, check=True)
    (artifacts / "properties.json").write_text(result.stdout, encoding="utf-8")
    settings = json.loads(result.stdout)["Properties"]
    baseline_version = settings["PackageValidationBaselineVersion"]
    if not baseline_version or settings["PackageId"] != "ZkLink" or any(
        settings[name].lower() != "true" for name in ["EnablePackageValidation",
            "ApiCompatEnableRuleCannotChangeParameterName", "ApiCompatEnableRuleAttributesMustMatch"]
    ):
        raise SystemExit("The released baseline and compatibility rules must be enabled.")
    installed = pathlib.Path(settings["NuGetPackageRoot"]) / "zklink" / baseline_version
    origin = json.loads((installed / ".nupkg.metadata").read_text(encoding="utf-8"))
    if origin["source"].rstrip("/") != PUBLIC_SOURCE.rstrip("/"):
        raise SystemExit("The compatibility baseline must come from the public NuGet feed.")
    baseline = artifacts / f"ZkLink.{baseline_version}.nupkg"
    shutil.copy2(installed / f"zklink.{baseline_version}.nupkg", baseline)
    with zipfile.ZipFile(baseline) as archive:
        metadata = ET.fromstring(archive.read("ZkLink.nuspec")).find("{*}metadata")
        if metadata.findtext("{*}id") != "ZkLink" or metadata.findtext("{*}version") != baseline_version:
            raise SystemExit("Unexpected baseline package identity.")
        baseline_assembly = archive.read(ASSEMBLY_ENTRY)
    validation = json.loads((run / "validation.json").read_text(encoding="utf-8"))
    candidate = run / "feed" / f"ZkLink.{validation['version']}.nupkg"
    if sha256(candidate) != validation["packageSha256"]:
        raise SystemExit("The compatibility candidate differs from the inspected package.")

    # Invoke the same SDK task without the pack target's incremental semaphore.
    # Resolve the same reference assemblies as ordinary core package validation.
    tools = pathlib.Path(settings["MSBuildToolsPath"])
    project = ET.Element("Project")
    ET.SubElement(project, "UsingTask", TaskName="Microsoft.DotNet.ApiCompat.Task.ValidatePackageTask",
                  AssemblyFile=str(pathlib.Path(settings["MSBuildSDKsPath"]) /
                                   "Microsoft.NET.Sdk/tools/net10.0/Microsoft.DotNet.ApiCompat.Task.dll"))
    target = ET.SubElement(project, "Target", Name="CheckCompatibility")
    references = ET.SubElement(target, "MSBuild", Projects=str(ROOT / "src/ZkLink/ZkLink.csproj"),
                               Targets="GetReferencesForApiCompatValidatePackage",
                               Properties="Configuration=Release;BuildProjectReferences=false")
    ET.SubElement(references, "Output", TaskParameter="TargetOutputs", ItemName="CompatibilityReferences")
    ET.SubElement(target, "Microsoft.DotNet.ApiCompat.Task.ValidatePackageTask",
                  PackageTargetPath="$(CandidatePackage)", BaselinePackageTargetPath=str(baseline),
                  RoslynAssembliesPath=str(tools / "Roslyn/bincore"),
                  RuntimeGraph=str(tools / "PortableRuntimeIdentifierGraph.json"),
                  PackageAssemblyReferences="@(CompatibilityReferences)",
                  EnableRuleAttributesMustMatch=settings["ApiCompatEnableRuleAttributesMustMatch"],
                  EnableRuleCannotChangeParameterName=settings["ApiCompatEnableRuleCannotChangeParameterName"])
    harness = artifacts / "check.proj"
    ET.ElementTree(project).write(harness, encoding="utf-8", xml_declaration=True)

    def check(name, package, expected_code=None):
        result = subprocess.run(["dotnet", "msbuild", str(harness), "-nologo", "-t:CheckCompatibility",
                                 f"-p:CandidatePackage={package}"], cwd=ROOT, capture_output=True,
                                text=True, timeout=COMMAND_GUARD_SECONDS, check=False)
        output = result.stdout + result.stderr
        (artifacts / f"{name}.txt").write_text(output, encoding="utf-8")
        if expected_code is None:
            if result.returncode != 0:
                raise SystemExit("Published API compatibility failed; inspect compatibility/positive.txt.")
        elif result.returncode != 1 or f"error {expected_code}:" not in output:
            raise SystemExit(f"Compatibility rejection check failed: {name}.")

    check("positive", candidate)
    mutations = [("removed-member", b"NoNode\0", b"NoNodX\0", "CP0002"),
                 ("renamed-parameter", b"cancellationToken\0", b"cancellationTokex\0", "CP0017")]
    for name, original, changed, code in mutations:
        package = artifacts / f"{name}.nupkg"
        with zipfile.ZipFile(candidate) as source, zipfile.ZipFile(package, "w") as output:
            for item in source.infolist():
                data = source.read(item)
                if item.filename == ASSEMBLY_ENTRY:
                    # Equal-length, unique UTF-8 metadata strings preserve the PE layout.
                    if len(original) != len(changed) or data.count(original) != 1:
                        raise SystemExit("The compatibility rejection fixture needs review.")
                    data = data.replace(original, changed)
                output.writestr(item, data)
        check(name, package, code)
    report = {"candidatePackageSha256": sha256(candidate), "baseline": {
        "id": "ZkLink", "version": baseline_version, "source": origin["source"],
        "package": str(baseline.relative_to(run)), "packageSha256": sha256(baseline),
        "assemblySha256": hashlib.sha256(baseline_assembly).hexdigest().upper()},
        "rules": {"parameterNames": True, "attributes": True, "compatibleAdditionsAllowed": True},
        "sdkComparisonPassed": True, "expectedRejections": {name: code for name, _, _, code in mutations},
        "artifacts": {str(path.relative_to(run)): sha256(path) for path in artifacts.iterdir() if path.is_file()},
        "scope": "SDK package/API rules; behavioral compatibility and final release qualification require separate evidence."}
    (run / "compatibility.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print("API compatibility: PASS (published baseline and expected breaking-change rejections)", flush=True)


if __name__ == "__main__":
    main()
