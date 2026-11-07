"""Format the solution split by where it can be evaluated.

Linux (default): every non-macOS project. `--macos`: the macOS projects, from the one host whose
workload can evaluate them.
"""

from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


macos = sys.argv[1:2] == ["--macos"]
args = sys.argv[2:] if macos else sys.argv[1:]
root = Path.cwd()
solution = ET.parse(root / "weavie.slnx")
mac_projects = []
for folder in solution.getroot():
    for project in list(folder):
        path = root / project.attrib["Path"]
        if "-macos" in ET.parse(path).findtext(".//TargetFramework", ""):
            folder.remove(project)
            mac_projects.append(f"{path.parent.relative_to(root)}/")
        else:
            project.set("Path", str(path))
if macos:
    subprocess.run(["dotnet", "format", "weavie.slnx", *args, "--include", *mac_projects], cwd=root, check=True)
    sys.exit()
(root / "temp").mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(dir=root / "temp") as scratch:
    target = Path(scratch) / "format.slnx"
    solution.write(target, encoding="unicode")
    subprocess.run(["dotnet", "format", str(target), *args], cwd=root, check=True)
