"""Format the evaluable solution on Linux and every macOS source file's whitespace."""

from pathlib import Path
import subprocess
import sys
import tempfile
import xml.etree.ElementTree as ET


root = Path(__file__).resolve().parents[2]
solution = ET.parse(root / "weavie.slnx")
mac_sources = []
for folder in solution.getroot():
    for project in list(folder):
        path = root / project.attrib["Path"]
        framework = ET.parse(path).findtext(".//TargetFramework", "")
        if "-macos" in framework:
            folder.remove(project)
            tracked = subprocess.check_output(
                ["git", "ls-files", "--", f"{path.parent.relative_to(root)}/*.cs"], cwd=root, text=True)
            mac_sources.extend(tracked.splitlines())
        else:
            project.set("Path", str(path))
(root / "temp").mkdir(exist_ok=True)
with tempfile.TemporaryDirectory(dir=root / "temp") as scratch:
    target = Path(scratch) / "format.slnx"
    solution.write(target, encoding="unicode")
    subprocess.run(["dotnet", "format", str(target), *sys.argv[1:]], cwd=root, check=True)
    if mac_sources:
        subprocess.run(["dotnet", "format", "whitespace", ".", "--folder", *sys.argv[1:],
                        "--include", *mac_sources], cwd=root, check=True)
