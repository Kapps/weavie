"""Pin release provenance and allocate a public version before any platform builds."""

import json
import os
from pathlib import Path
import re
import xml.etree.ElementTree as ET

from github_api import GitHub

VERSION = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")


def version_tuple(tag):
    match = VERSION.fullmatch(tag)
    if match is None:
        raise ValueError(f"Invalid version tag: {tag}")
    return tuple(map(int, match.groups()))


def next_version(current, bump):
    parts = list(version_tuple(f"v{current}"))
    index = {"major": 0, "minor": 1, "patch": 2}[bump]
    parts[index] += 1
    parts[index + 1:] = [0] * (2 - index)
    return ".".join(map(str, parts))


def build_plan(api, number):
    name = f"build-{number}"
    reference = api.get(f"git/ref/tags/{name}")["object"]
    if reference["type"] != "tag":
        raise ValueError(f"{name} has no annotated build provenance")
    tag = api.get(f"git/tags/{reference['sha']}")
    plan = json.loads(tag["message"])
    if (tag["tag"] != name or plan["build"] != int(number)
            or tag["object"]["type"] != "commit" or tag["object"]["sha"] != plan["commit"]):
        raise ValueError(f"{name} provenance does not match its tag")
    return plan


def source_commit(api, source):
    if not source:
        return api.get("git/ref/heads/main")["object"]["sha"]
    if re.fullmatch(r"[1-9][0-9]*", source):
        return build_plan(api, source)["commit"]
    if SHA.fullmatch(source):
        return api.get(f"commits/{source}")["sha"]
    raise ValueError("Source must be a published build number or a full 40-character commit SHA")


def make_plan(api, event, event_name, channel, bump, source, build, baseline):
    if event_name == "workflow_run":
        run = event["workflow_run"]
        if (run["event"] != "push" or run["conclusion"] != "success"
                or run["head_branch"] != "main"
                or run["head_repository"]["full_name"] != os.environ["GITHUB_REPOSITORY"]):
            raise ValueError("Only successful main-push CI may publish automatically")
        commit = run["head_sha"]
        channel = "latest"
    elif event_name == "workflow_dispatch":
        if channel not in ("stable", "latest"):
            raise ValueError("Channel must be stable or latest")
        commit = source_commit(api, source)
    else:
        raise ValueError("Release requires a manual dispatch or successful main-push CI")
    if SHA.fullmatch(commit) is None:
        raise ValueError("Release source is not a full commit SHA")
    main = api.get("git/ref/heads/main")["object"]["sha"]
    if api.get(f"compare/{commit}...{main}")["status"] not in ("ahead", "identical"):
        raise ValueError("Release source must belong to main's history")
    versions = [release["tag_name"] for release in api.pages("releases")
                if not release["draft"] and not release["prerelease"]
                and VERSION.fullmatch(release["tag_name"])]
    current = max(versions, key=version_tuple)[1:] if versions else baseline
    version = next_version(current, bump) if channel == "stable" else current
    return {"commit": commit, "build": build, "version": version, "channel": channel,
            "automatic": event_name == "workflow_run"}


def main():
    # Re-running failed jobs preserves prepare's outputs; re-running all jobs could reallocate a version.
    if os.environ["GITHUB_RUN_ATTEMPT"] != "1":
        raise ValueError("Re-run failed jobs to resume a release, or start a new workflow to rebuild")
    api = GitHub()
    event = json.loads(Path(os.environ["GITHUB_EVENT_PATH"]).read_text())
    baseline = ET.parse("Directory.Build.props").findtext(".//PublicVersion")
    plan = make_plan(api, event, os.environ["GITHUB_EVENT_NAME"], os.environ["RELEASE_CHANNEL"],
                     os.environ["RELEASE_BUMP"], os.environ["RELEASE_SOURCE"],
                     int(os.environ["GITHUB_RUN_NUMBER"]), baseline)
    Path("release-plan.json").write_text(json.dumps(plan, indent=2) + "\n")
    matrix = {"platform": json.loads(Path(".github/platforms.json").read_text())}
    with open(os.environ["GITHUB_OUTPUT"], "a") as output:
        output.write(f"matrix={json.dumps(matrix)}\n")
        for key in ("commit", "build", "version", "channel"):
            output.write(f"{key}={plan[key]}\n")
    with open(os.environ["GITHUB_STEP_SUMMARY"], "a") as summary:
        subject = api.get(f"commits/{plan['commit']}")["commit"]["message"].splitlines()[0]
        url = f"https://github.com/{os.environ['GITHUB_REPOSITORY']}/commit/{plan['commit']}"
        summary.write(f"## {plan['channel']} {plan['version']}.{plan['build']}\n\n"
                      f"Builds [`{plan['commit'][:8]}`]({url}): {subject}\n\n"
                      "This run's own commit only supplies the release scripts.\n\n"
                      "Publication requires complete packages. Release check failures remain visible "
                      "but do not block publication.\n")


if __name__ == "__main__":
    main()
