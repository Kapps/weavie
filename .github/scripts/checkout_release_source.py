"""Authorize a release source against trusted main history before checking out any of its files."""

import os
from pathlib import Path
import re
import subprocess


def checkout_source(root, source):
    if re.fullmatch(r"[0-9a-f]{40}", source) is None:
        raise ValueError("Release checkout requires a full 40-character commit SHA")
    result = subprocess.run(["git", "merge-base", "--is-ancestor", source, "refs/remotes/origin/main"],
                            cwd=root, capture_output=True, text=True)
    if result.returncode == 1:
        raise ValueError("Release source is not in trusted main history; refusing to check out its files")
    if result.returncode != 0:
        raise RuntimeError(f"Unable to verify release ancestry: {result.stderr}")
    hooks = root / "temp" / "release-no-hooks"
    hooks.mkdir(parents=True, exist_ok=True)
    subprocess.run(["git", "-c", f"core.hooksPath={hooks}", "checkout", "--detach", source],
                   cwd=root, check=True)
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, text=True).strip()
    if actual != source:
        raise ValueError("Release checkout did not produce the authorized commit")


if __name__ == "__main__":
    checkout_source(Path.cwd(), os.environ["SOURCE_COMMIT"])
