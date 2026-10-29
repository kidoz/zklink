"""Regression checks for release tags and their source/changelog evidence."""

import pathlib
import tempfile
import unittest

from release_version import local_candidate_version, validate_release, validate_version


class ReleaseVersionTests(unittest.TestCase):
    def test_local_candidates_keep_the_project_version_and_are_unique(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            project = root / "src/ZkLink/ZkLink.csproj"
            project.parent.mkdir(parents=True)
            project.write_text("<Project><PropertyGroup><VersionPrefix>1.2.3</VersionPrefix>"
                               "<VersionSuffix>rc.1</VersionSuffix></PropertyGroup></Project>", encoding="utf-8")
            revision = "a" * 40
            first = local_candidate_version(revision, root)
            self.assertEqual(first, validate_version(first))
            self.assertTrue(first.startswith("1.2.3-local.raaaaaaaaaaaa.n"))
            self.assertNotEqual(first, local_candidate_version(revision, root))
            for bad in ["short", "a" * 39, "z" * 40, "a" * 39 + "/"]:
                with self.subTest(revision=bad), self.assertRaises(ValueError):
                    local_candidate_version(bad, root)

    def test_accepts_stable_and_prerelease_versions(self):
        for version in ["0.1.0", "1.2.3-rc.1", "1.0.0-0", "1.0.0-alpha-beta"]:
            with self.subTest(version=version):
                self.assertEqual(version, validate_version(version))

    def test_rejects_invalid_or_normalized_versions(self):
        for version in ["", "v0.1.0", "0.1", "01.2.3", "1.02.3", "1.2.03",
                        "1.2.3-", "1.2.3-01", "1.2.3-rc..1", "1.2.3+build.1",
                        "1.2.3\n", "1.2.3-rc/1", "1.2.3;echo injected"]:
            with self.subTest(version=version), self.assertRaises(ValueError):
                validate_version(version)

    def test_release_requires_matching_source_and_dated_changelog(self):
        with tempfile.TemporaryDirectory() as directory:
            root = pathlib.Path(directory)
            project = root / "src/ZkLink/ZkLink.csproj"
            project.parent.mkdir(parents=True)
            project.write_text("<Project><PropertyGroup><VersionPrefix>0.1.0</VersionPrefix>"
                               "</PropertyGroup></Project>", encoding="utf-8")
            changelog = root / "CHANGELOG.md"
            changelog.write_text("## [Unreleased]\n\n## [0.1.0] - 2026-10-28\n", encoding="utf-8")
            self.assertEqual("0.1.0", validate_release("v0.1.0", root))
            for tag in ["0.1.0", "v0.2.0", "v0.1.0-rc.1"]:
                with self.subTest(tag=tag), self.assertRaises(ValueError):
                    validate_release(tag, root)
            changelog.write_text("Mention ## [0.1.0] in text only.\n", encoding="utf-8")
            with self.assertRaises(ValueError):
                validate_release("v0.1.0", root)


if __name__ == "__main__":
    unittest.main()
