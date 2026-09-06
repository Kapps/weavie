import json
import os
import unittest
from unittest.mock import patch

from release_plan import build_plan, make_plan, next_version, source_commit

COMMIT = "a" * 40
MAIN = "b" * 40


class FakeGitHub:
    def __init__(self):
        self.responses = {
            "git/ref/heads/main": {"object": {"sha": MAIN}},
            "git/ref/tags/main-latest": {"object": {"sha": COMMIT, "type": "commit"}},
            f"commits/{COMMIT}": {"sha": COMMIT},
            f"compare/{COMMIT}...{MAIN}": {"status": "ahead"},
        }
        self.releases = []

    def get(self, path):
        return self.responses[path]

    def pages(self, path):
        assert path == "releases"
        return iter(self.releases)


class ReleasePlanTests(unittest.TestCase):
    def test_version_bump_resets_lower_components(self):
        for bump, expected in (("patch", "1.4.3"), ("minor", "1.5.0"), ("major", "2.0.0")):
            with self.subTest(bump=bump):
                self.assertEqual(expected, next_version("1.4.2", bump))

    def test_only_published_versions_allocate_the_next_version(self):
        api = FakeGitHub()
        api.releases = [
            {"tag_name": tag, "draft": draft, "prerelease": pre}
            for tag, draft, pre in (("v0.9.0", False, False), ("v0.10.0", False, False),
                                    ("v1.0.0", True, False), ("v2.0.0", False, True),
                                    ("main-latest", False, True))
        ]
        plan = make_plan(api, {}, "workflow_dispatch", "stable", "patch", "", 1507, "0.1.0")
        self.assertEqual("0.10.1", plan["version"])
        self.assertEqual(COMMIT, plan["commit"])
        self.assertEqual(1507, plan["build"])

    def test_first_release_uses_the_declared_baseline(self):
        plan = make_plan(FakeGitHub(), {}, "workflow_dispatch", "stable", "minor", "", 1507, "0.1.0")
        self.assertEqual("0.2.0", plan["version"])

    def test_latest_selects_a_commit_without_bumping_public_version(self):
        plan = make_plan(FakeGitHub(), {}, "workflow_dispatch", "latest", "major", COMMIT, 1507, "0.1.0")
        self.assertEqual("0.1.0", plan["version"])
        self.assertFalse(plan["automatic"])

    def test_numbered_source_uses_recorded_provenance(self):
        api = FakeGitHub()
        api.responses["git/ref/tags/build-1507"] = {"object": {"type": "tag", "sha": "tag-object"}}
        api.responses["git/tags/tag-object"] = {
            "tag": "build-1507", "object": {"type": "commit", "sha": COMMIT},
            "message": json.dumps({"build": 1507, "commit": COMMIT}),
        }
        self.assertEqual(COMMIT, source_commit(api, "1507"))
        api.responses["git/tags/tag-object"]["object"]["sha"] = MAIN
        with self.assertRaisesRegex(ValueError, "provenance"):
            build_plan(api, "1507")

    def test_invalid_sources_are_rejected_before_api_access(self):
        for source in ("main", "v0.1.0", "abc123", "../main", "$(echo secret)", "-1"):
            with self.subTest(source=source), self.assertRaisesRegex(ValueError, "Source must"):
                source_commit(FakeGitHub(), source)

    def test_source_must_belong_to_main_history(self):
        api = FakeGitHub()
        api.responses[f"compare/{COMMIT}...{MAIN}"] = {"status": "diverged"}
        with self.assertRaisesRegex(ValueError, "main's history"):
            make_plan(api, {}, "workflow_dispatch", "stable", "patch", COMMIT, 1507, "0.1.0")

    @patch.dict(os.environ, {"GITHUB_REPOSITORY": "acme/weavie"})
    def test_automatic_publication_requires_trusted_successful_main_push(self):
        run = {"event": "push", "conclusion": "success", "head_branch": "main", "head_sha": COMMIT,
               "head_repository": {"full_name": "acme/weavie"}}
        plan = make_plan(FakeGitHub(), {"workflow_run": run}, "workflow_run", "", "", "", 1507, "0.1.0")
        self.assertEqual(("latest", COMMIT, True), (plan["channel"], plan["commit"], plan["automatic"]))
        for key, value in (("event", "pull_request"), ("conclusion", "failure"), ("head_branch", "other"),
                           ("head_repository", {"full_name": "fork/weavie"})):
            with self.subTest(key=key), self.assertRaisesRegex(ValueError, "successful main-push"):
                make_plan(FakeGitHub(), {"workflow_run": {**run, key: value}}, "workflow_run",
                          "", "", "", 1507, "0.1.0")


if __name__ == "__main__":
    unittest.main()
