from contextlib import chdir
from pathlib import Path
import runpy
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
import xml.etree.ElementTree as ET


SCRIPT = Path(__file__).with_name("format-dotnet.py").resolve()


class FormatDotnetTests(unittest.TestCase):
    def run_script(self, *args):
        """Runs the helper over an App + Mac solution; returns each dotnet command after the first two words."""
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch).resolve()
            (root / "weavie.slnx").write_text(
                '<Solution><Folder Name="/src/">'
                '<Project Path="App/App.csproj" />'
                '<Project Path="Mac/Mac.csproj" />'
                '</Folder></Solution>')
            for name, framework in (("App", "net10.0"), ("Mac", "net10.0-macos")):
                (root / name).mkdir()
                (root / name / f"{name}.csproj").write_text(
                    f'<Project><PropertyGroup><TargetFramework>{framework}</TargetFramework>'
                    '</PropertyGroup></Project>')
            commands = []

            def format_command(command, **kwargs):
                self.assertEqual(root, kwargs["cwd"])
                self.assertTrue(kwargs["check"])
                self.assertEqual(["dotnet", "format"], command[:2])
                solution = command[2]
                projects = [p.attrib["Path"] for p in ET.parse(root / solution).findall(".//Project")]
                commands.append((projects, command[3:]))
                raise subprocess.CalledProcessError(2, command)

            with chdir(root), patch.object(sys, "argv", [str(SCRIPT), *args]), \
                    patch("subprocess.run", side_effect=format_command):
                with self.assertRaises(subprocess.CalledProcessError):
                    runpy.run_path(str(SCRIPT), run_name="__main__")
            return root, commands

    def test_linux_formats_every_non_macos_project_and_propagates_failures(self):
        root, commands = self.run_script("--verify-no-changes")
        self.assertEqual([([str(root / "App" / "App.csproj")], ["--verify-no-changes"])], commands)

    def test_macos_formats_only_the_macos_projects(self):
        _, commands = self.run_script("--macos", "--verify-no-changes", "--no-restore")
        self.assertEqual([(["App/App.csproj", "Mac/Mac.csproj"],
                           ["--verify-no-changes", "--no-restore", "--include", "Mac/"])], commands)


if __name__ == "__main__":
    unittest.main()
