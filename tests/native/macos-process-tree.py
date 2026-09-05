#!/usr/bin/env python3
"""Exercise managed tree termination in a real LaunchServices GUI process group."""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess
import tempfile

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("app", type=Path)
parser.add_argument("--control", action="store_true", help="Also measure an unisolated disposable app")
args = parser.parse_args()
app = args.app.resolve(strict=True)


def probe(owned):
    with tempfile.TemporaryDirectory(prefix="weavie-gui-process-") as directory:
        result = Path(directory) / "result.json"
        mode = "--owned-process-probe" if owned else "--unisolated-process-probe"
        launch = subprocess.Popen(["/usr/bin/open", "-n", "-W", str(app), "--args", mode, str(result)])
        try:
            try:
                code = launch.wait(timeout=30)
            except subprocess.TimeoutExpired:
                code = "timeout"
            state = json.loads(result.read_text()) if result.exists() else {}
            print(json.dumps({"owned": owned, "openExit": code, **state}), flush=True)
            if owned and (code != 0 or state.get("phase") != "survived"):
                raise RuntimeError("GUI host did not survive isolated process-tree termination")
            if not owned and state.get("phase") not in ("killing", "survived"):
                raise RuntimeError("Unisolated control did not reach tree termination")
        finally:
            if result.exists():
                state = json.loads(result.read_text())
                # Never signal the runner's group: only the verified LaunchServices leader's tree.
                host = state.get("host")
                if host and state.get("hostGroup") == host and state.get("phase") != "survived":
                    for pid in (state.get("descendant"), state.get("child"), host):
                        if pid:
                            try:
                                os.kill(pid, signal.SIGKILL)
                            except ProcessLookupError:
                                pass
            if launch.poll() is None:
                launch.kill()
            launch.wait()


probe(True)
if args.control:
    probe(False)
