#!/usr/bin/env python3
"""Reject incomplete release assets before downloading or entering publication."""

import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("qualified_release", Path(__file__).with_name("download-qualified-release.py"))
release = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release)


class QualifiedReleaseTests(unittest.TestCase):
    def setUp(self):
        self.metadata = {"tagName": "v1.0.0", "assets": [
            {"name": name, "size": 1} for name in release.required_assets("1.0.0")]}

    def check(self):
        with patch.object(release, "gh", return_value=json.dumps(self.metadata)) as gh:
            release.check_assets("kidoz/zklink", "v1.0.0", "1.0.0")
        gh.assert_called_once_with("release", "view", "v1.0.0", "--repo", "kidoz/zklink",
                                   "--json", "tagName,assets", capture=True)

    def test_complete_asset_inventory_accepts_draft_metadata(self):
        self.check()

    def test_empty_release_names_every_missing_asset(self):
        self.metadata["assets"] = []
        with self.assertRaises(ValueError) as failure:
            self.check()
        for name in release.required_assets("1.0.0"):
            self.assertIn(name, str(failure.exception))

    def test_each_required_asset_must_exist_and_be_nonempty(self):
        for name in release.required_assets("1.0.0"):
            with self.subTest(name=name):
                complete = self.metadata["assets"]
                self.metadata["assets"] = [asset for asset in complete if asset["name"] != name]
                with self.assertRaisesRegex(ValueError, "missing qualified assets"):
                    self.check()
                self.metadata["assets"].append({"name": name, "size": 0})
                with self.assertRaisesRegex(ValueError, "missing qualified assets"):
                    self.check()
                self.metadata["assets"] = complete

    def test_other_version_packages_do_not_satisfy_the_release(self):
        self.metadata["assets"] = [{"name": asset["name"].replace("1.0.0", "0.1.0"), "size": 1}
                                   for asset in self.metadata["assets"]]
        with self.assertRaisesRegex(ValueError, "ZkLink.1.0.0.nupkg"):
            self.check()

    def test_wrong_tag_is_rejected(self):
        self.metadata["tagName"] = "v0.1.0"
        with self.assertRaisesRegex(ValueError, "tag mismatch"):
            self.check()

    def test_missing_assets_prevent_download_and_artifact_creation(self):
        with tempfile.TemporaryDirectory() as temporary:
            self.metadata["assets"] = []
            with (patch.object(release, "ROOT", Path(temporary)),
                  patch.object(release, "gh", return_value=json.dumps(self.metadata)) as gh,
                  patch.dict(os.environ, {"PACKAGE_VERSION": "1.0.0", "GITHUB_REPOSITORY": "kidoz/zklink",
                                          "QUALIFIED_RELEASE_TAG": "v1.0.0"}),
                  patch("sys.argv", ["download-qualified-release.py"])):
                with self.assertRaisesRegex(ValueError, "missing qualified assets"):
                    release.main()
                self.assertEqual(gh.call_count, 1)
                self.assertFalse((Path(temporary) / "artifacts").exists())


if __name__ == "__main__":
    unittest.main()
