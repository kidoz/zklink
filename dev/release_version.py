"""Validate the NuGet version, release tag, source version and changelog together."""

import argparse
import pathlib
import re
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
SEMVER = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?")


def validate_version(value):
    match = SEMVER.fullmatch(value)
    if not match or any(
        part.isdigit() and len(part) > 1 and part.startswith("0")
        for part in (match.group(4) or "").split(".")
    ):
        raise ValueError("Expected MAJOR.MINOR.PATCH with optional SemVer prerelease identifiers.")
    return value


def validate_release(tag, root=ROOT):
    if not tag.startswith("v"):
        raise ValueError("Release tags must start with v.")
    version = validate_version(tag[1:])
    project = ET.parse(root / "src/ZkLink/ZkLink.csproj").getroot()
    prefix = project.findtext(".//VersionPrefix")
    suffix = project.findtext(".//VersionSuffix")
    expected = f"{prefix}-{suffix}" if suffix else prefix
    if version != expected:
        raise ValueError("Release tag does not match the core project's version.")
    changelog = (root / "CHANGELOG.md").read_text(encoding="utf-8")
    heading = rf"^## \[{re.escape(version)}\] - [0-9]{{4}}-[0-9]{{2}}-[0-9]{{2}}\s*$"
    if not re.search(heading, changelog, re.MULTILINE):
        raise ValueError("CHANGELOG.md must contain a dated heading for this release version.")
    return version


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--tag")
    mode.add_argument("--version")
    mode.add_argument("--manual", action="store_true")
    args = parser.parse_args()
    try:
        version = "0.0.0-manual" if args.manual else (
            validate_release(args.tag) if args.tag is not None else validate_version(args.version)
        )
    except ValueError as error:
        parser.error(str(error))
    print(version)


if __name__ == "__main__":
    main()
