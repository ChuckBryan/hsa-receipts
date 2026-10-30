from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).resolve().parents[1] / ".github" / "scripts" / "update-version.py"
PROPERTIES = """<Project>
  <!-- Shared build settings -->
  <PropertyGroup>
    <Version>1.0.0</Version>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
</Project>
"""


class ReleaseVersionTests(unittest.TestCase):
    def setUp(self):
        directory = tempfile.TemporaryDirectory()
        self.addCleanup(directory.cleanup)
        self.directory = Path(directory.name)
        self.properties = self.directory / "Directory.Build.props"
        self.properties.write_text(PROPERTIES, encoding="utf-8")

    def run_update(self, version):
        return subprocess.run(
            [sys.executable, str(SCRIPT), version, "--file", str(self.properties)],
            capture_output=True,
            text=True,
            check=False,
        )

    def test_updates_version_and_preserves_other_settings(self):
        for version in ("1.2.3", "2.0.0-rc.1", "2.0.0+build.5"):
            with self.subTest(version=version):
                result = self.run_update(version)
                self.assertEqual(result.returncode, 0, result.stderr)
                tree = ET.parse(self.properties)
                self.assertEqual(tree.findtext("./PropertyGroup/Version"), version)
                self.assertEqual(tree.findtext("./PropertyGroup/TreatWarningsAsErrors"), "true")
                self.assertIn("<!-- Shared build settings -->", self.properties.read_text(encoding="utf-8"))

    def test_repeated_update_is_idempotent(self):
        self.assertEqual(self.run_update("1.2.3").returncode, 0)
        first_result = self.properties.read_bytes()
        self.assertEqual(self.run_update("1.2.3").returncode, 0)
        self.assertEqual(self.properties.read_bytes(), first_result)

    def test_missing_or_duplicate_version_fails_without_modifying_file(self):
        for elements in ("", "<Version>1.0.0</Version><Version>2.0.0</Version>"):
            with self.subTest(elements=elements):
                self.properties.write_text(f"<Project><PropertyGroup>{elements}</PropertyGroup></Project>", encoding="utf-8")
                original = self.properties.read_bytes()
                result = self.run_update("1.2.3")
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Expected exactly one", result.stderr)
                self.assertEqual(self.properties.read_bytes(), original)

    def test_malformed_xml_fails_without_modifying_file(self):
        self.properties.write_text("<Project>", encoding="utf-8")
        result = self.run_update("1.2.3")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("ParseError", result.stderr)
        self.assertEqual(self.properties.read_text(encoding="utf-8"), "<Project>")

    def test_empty_version_fails_without_modifying_file(self):
        original = self.properties.read_bytes()
        result = self.run_update("")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("Version must not be empty", result.stderr)
        self.assertEqual(self.properties.read_bytes(), original)

    def test_default_path_is_relative_to_script_not_working_directory(self):
        script_copy = self.directory / ".github" / "scripts" / "update-version.py"
        script_copy.parent.mkdir(parents=True)
        shutil.copyfile(SCRIPT, script_copy)
        result = subprocess.run(
            [sys.executable, str(script_copy), "1.2.3"],
            cwd=script_copy.parent,
            capture_output=True,
            text=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(ET.parse(self.properties).findtext("./PropertyGroup/Version"), "1.2.3")


if __name__ == "__main__":
    unittest.main()