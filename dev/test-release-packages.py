"""Reject incomplete, mismatched or substituted coordinated release packages."""
import importlib.util
import json
from pathlib import Path
import unittest
import zipfile

import release_candidate

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, Path(__file__).with_name(path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

release = load("release_packages", "release-packages.py")
fixtures = load("release_evidence_tests", "test-release-evidence.py")


class ReleasePackageTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.ReleaseEvidenceTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.feed = self.fixture.feed
        for package_id, symbols in release.PACKAGES.items():
            self.package(package_id)
            if symbols:
                self.package(package_id, symbols=True)
        release_candidate.write_candidate(self.feed, "1.0.0", self.fixture.REVISION)

    def package(self, package_id, *, symbols=False, version="1.0.0", core_dependency="1.0.0", extra=None):
        dependencies = ""
        if package_id == "ZkLink.Extensions.DependencyInjection":
            dependencies = (f'<dependency id="ZkLink" version="{core_dependency}"/>'
                            '<dependency id="Microsoft.Extensions.DependencyInjection.Abstractions" version="10.0.14"/>'
                            '<dependency id="Microsoft.Extensions.Hosting.Abstractions" version="10.0.14"/>')
        metadata = (f'<package><metadata><id>{package_id}</id><version>{version}</version><license>MIT</license>'
                    f'<repository url="{release.REPOSITORY}" commit="{self.fixture.REVISION}"/>'
                    f'<dependencies>{dependencies}</dependencies></metadata></package>')
        suffix = ".snupkg" if symbols else ".nupkg"
        path = self.feed / (package_id + ".1.0.0" + suffix)
        with zipfile.ZipFile(path, "w") as archive:
            archive.writestr(package_id + ".nuspec", metadata)
            if symbols:
                archive.writestr("lib/net10.0/" + package_id + ".pdb", b"synthetic PDB")
            else:
                for name in ["README.md", "LICENSE", "THIRD-PARTY-NOTICES.md"]:
                    archive.writestr(name, "synthetic fixture")
                if package_id == "ZkLink.Analyzers":
                    archive.writestr("analyzers/dotnet/cs/ZkLink.Analyzers.dll", b"synthetic analyzer")
                else:
                    for suffix in [".dll", ".xml"]:
                        archive.writestr("lib/net10.0/" + package_id + suffix, b"synthetic asset")
                if extra:
                    archive.writestr(extra, b"unexpected asset")

    def test_complete_set_has_three_packages_and_two_symbols(self):
        record = release.check_packages(self.feed, "1.0.0")
        self.assertEqual(len(record["files"]), 5)
        self.assertEqual(record["revision"], self.fixture.REVISION)

    def test_missing_or_extra_package_is_rejected(self):
        (self.feed / "unexpected.1.0.0.nupkg").write_bytes(b"extra")
        with self.assertRaisesRegex(ValueError, "exactly three"):
            release.check_packages(self.feed, "1.0.0")
        (self.feed / "unexpected.1.0.0.nupkg").unlink()
        (self.feed / "ZkLink.Analyzers.1.0.0.nupkg").unlink()
        with self.assertRaisesRegex(ValueError, "exactly three"):
            release.check_packages(self.feed, "1.0.0")

    def test_optional_package_version_mismatch_is_rejected(self):
        self.package("ZkLink.Analyzers", version="1.0.1")
        with self.assertRaisesRegex(ValueError, "version mismatch"):
            release.check_packages(self.feed, "1.0.0")

    def test_di_cannot_depend_on_an_older_core(self):
        self.package("ZkLink.Extensions.DependencyInjection", core_dependency="0.1.0")
        with self.assertRaisesRegex(ValueError, "different core version"):
            release.check_packages(self.feed, "1.0.0")

    def test_analyzers_cannot_ship_as_a_runtime_library(self):
        self.package("ZkLink.Analyzers", extra="lib/net10.0/ZkLink.Analyzers.dll")
        with self.assertRaisesRegex(ValueError, "analyzer assets"):
            release.check_packages(self.feed, "1.0.0")

    def test_substituted_optional_package_is_rejected(self):
        record = release.check_packages(self.feed, "1.0.0")
        manifest = self.feed / release.MANIFEST
        manifest.write_text(json.dumps(record))
        digest = release_candidate.sha256(manifest)
        self.assertEqual(record, release.verify_manifest(self.feed, digest))
        with zipfile.ZipFile(self.feed / "ZkLink.Analyzers.1.0.0.nupkg", "a") as archive:
            archive.writestr("extra.txt", "changed package")
        with self.assertRaisesRegex(ValueError, "identity mismatch"):
            release.verify_manifest(self.feed, digest)


if __name__ == "__main__":
    unittest.main()
