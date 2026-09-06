"""Publish complete immutable releases, then atomically advance the stable tag."""

import json
import os
from pathlib import Path
import subprocess
import tarfile
import tempfile

from github_api import GitHub
from release_plan import version_tuple


def immutable_tag(api, name, plan):
    existing = api.optional(f"git/ref/tags/{name}")
    if existing is not None:
        target = existing["object"]
        if target["type"] != "tag":
            raise ValueError(f"{name} is not an annotated release tag")
        tag = api.get(f"git/tags/{target['sha']}")
        if (tag["tag"] != name or tag["object"]["type"] != "commit"
                or tag["object"]["sha"] != plan["commit"] or json.loads(tag["message"]) != plan):
            raise ValueError(f"Refusing to replace immutable tag {name}")
        return target["sha"]
    tag = api.request("POST", "git/tags", {"tag": name, "message": json.dumps(plan, sort_keys=True),
                                         "object": plan["commit"], "type": "commit"})
    api.request("POST", "git/refs", {"ref": f"refs/tags/{name}", "sha": tag["sha"]})
    return tag["sha"]


def find_release(api, tag):
    published = api.optional(f"releases/tags/{tag}")
    if published is not None:
        return published
    return next((release for release in api.pages("releases") if release["tag_name"] == tag), None)


def publish_stable(api, plan, assets, run_command):
    tag = f"v{plan['version']}"
    existing = find_release(api, tag)
    tag_sha = immutable_tag(api, tag, plan)
    try:
        if existing is None or existing["draft"]:
            if existing is not None:
                api.request("DELETE", f"releases/{existing['id']}", None)
            run_command(["gh", "release", "create", tag, "--draft", "--verify-tag", "--generate-notes",
                         "--title", f"Weavie {plan['version']} (build {plan['build']})", *map(str, assets)])
            run_command(["gh", "release", "edit", tag, "--draft=false", "--latest=false"])
        else:
            expected = {path.name for path in assets}
            if existing["prerelease"] or {asset["name"] for asset in existing["assets"]} != expected:
                raise ValueError(f"Published {tag} does not contain the complete stable release")
    except Exception:
        # An unpublished attempt must not reserve a public version; a published version is immutable.
        current = find_release(api, tag)
        if current is None or current["draft"]:
            if current is not None:
                api.request("DELETE", f"releases/{current['id']}", None)
            api.request("DELETE", f"git/refs/tags/{tag}", None)
        raise
    current = api.optional("git/ref/tags/stable")
    if current is not None:
        target = current["object"]
        if target["type"] != "tag":
            raise ValueError("stable must identify an annotated version tag")
        current_tag = api.get(f"git/tags/{target['sha']}")
        current_plan = json.loads(current_tag["message"])
        if (version_tuple(current_tag["tag"]) > version_tuple(tag)
                or current_plan["build"] > plan["build"]):
            raise ValueError(f"Release {tag} was superseded by stable {current_tag['tag']}; refusing to rewind")
    # Readers pin the annotated version object before looking up its immutable release assets.
    api.set_ref("stable", tag_sha)
    run_command(["gh", "release", "edit", tag, "--latest"])


def ensure_latest_advances(api, plan, run_command):
    if api.optional("releases/tags/main-latest") is None:
        return
    # The published runner manifest is the authoritative build identity used by the updater itself.
    with tempfile.TemporaryDirectory() as scratch:
        run_command(["gh", "release", "download", "main-latest", "--pattern",
                     "weavie-runner-linux-x64.tar.gz", "--dir", scratch])
        with tarfile.open(Path(scratch) / "weavie-runner-linux-x64.tar.gz") as archive:
            manifests = [member for member in archive.getmembers()
                         if len(Path(member.name).parts) == 3 and Path(member.name).parts[0] == "versions"
                         and Path(member.name).name == "manifest.json" and member.isfile()]
            if len(manifests) != 1:
                raise ValueError("Published latest runner must contain exactly one version manifest")
            current = json.load(archive.extractfile(manifests[0]))["buildNumber"]
    if current >= plan["build"]:
        raise ValueError(f"Latest build {current} supersedes build {plan['build']}; start a new workflow to rebuild")


def main():
    plan = json.loads(Path("release-assets/release-plan.json").read_text())
    api = GitHub()
    platforms = json.loads(Path(".github/platforms.json").read_text())
    names = ["weavie-runner-linux-x64.tar.gz", *[p["asset"] for p in platforms
             if plan["channel"] == "stable" or p["name"] == "linux"]]
    assets = [Path("release-assets") / name for name in (*names, "release-plan.json")]
    for asset in assets:
        if not asset.is_file():
            raise ValueError(f"Missing release asset: {asset}")
    if plan["channel"] == "latest":
        ensure_latest_advances(api, plan, lambda command: subprocess.run(command, check=True))
    immutable_tag(api, f"build-{plan['build']}", plan)
    if plan["channel"] == "stable":
        publish_stable(api, plan, assets, lambda command: subprocess.run(command, check=True))
        link = f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/tag/v{plan['version']}"
    elif plan["channel"] == "latest":
        subprocess.run(["bash", ".github/scripts/publish-main-latest.sh", plan["commit"],
                        f"main-latest-{os.environ['GITHUB_RUN_ID']}", os.environ["GITHUB_API_URL"],
                        os.environ["GITHUB_REPOSITORY"], f"{plan['version']}.{plan['build']}",
                        "release-assets", str(plan["automatic"]).lower()], check=True)
        link = f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/releases/tag/main-latest"
    else:
        raise ValueError("Unknown release channel")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as summary:
        summary.write(f"\n[Download {plan['channel']} {plan['version']}.{plan['build']}]({link})\n")


if __name__ == "__main__":
    main()
