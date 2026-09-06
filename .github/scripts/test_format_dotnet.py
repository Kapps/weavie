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
    def test_trusted_helper_formats_the_source_checkout_and_propagates_failures(self):
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
            (root / "Mac" / "Tracked.cs").touch()
            subprocess.run(["git", "init", str(root)], check=True, capture_output=True)
            subprocess.run(["git", "add", "."], cwd=root, check=True)
            (root / "Mac" / "Untracked.cs").touch()

            run_process = subprocess.run

            def format_command(command, **kwargs):
                if command[0] == "git":
                    return run_process(command, **kwargs)
                self.assertEqual(root, kwargs["cwd"])
                self.assertTrue(kwargs["check"])
                self.assertEqual(["dotnet", "format"], command[:2])
                self.assertIn("--verify-no-changes", command)
                if command[2] == "whitespace":
                    self.assertEqual(["Mac/Tracked.cs"], command[command.index("--include") + 1:])
                    raise subprocess.CalledProcessError(2, command)
                solution = ET.parse(command[2])
                self.assertEqual([str(root / "App" / "App.csproj")],
                                 [p.attrib["Path"] for p in solution.findall(".//Project")])

            with chdir(root), patch.object(sys, "argv", [str(SCRIPT), "--verify-no-changes"]), \
                    patch("subprocess.run", side_effect=format_command) as run:
                with self.assertRaises(subprocess.CalledProcessError):
                    runpy.run_path(str(SCRIPT), run_name="__main__")
                self.assertEqual(2, sum(call.args[0][0] == "dotnet" for call in run.call_args_list))


if __name__ == "__main__":
    unittest.main()
