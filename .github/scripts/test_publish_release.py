import io
import json
import tarfile
from pathlib import Path
import unittest

from publish_release import ensure_latest_advances, immutable_tag, publish_stable

PLAN = {"commit": "a" * 40, "build": 1507, "version": "0.2.1", "channel": "stable", "automatic": False}
ASSETS = [Path("runner.tar.gz"), Path("windows.zip"), Path("macos.zip"), Path("release-plan.json")]


class FakeGitHub:
    def __init__(self):
        self.refs = {"stable": {"object": {"sha": "previous-stable", "type": "tag"}}}
        self.tags = {"previous-stable": {"tag": "v0.2.0", "message": json.dumps({"build": 1506})}}
        self.release = None
        self.operations = []
        self.fail_upload = False
        self.fail_promote = False

    def optional(self, path):
        if path.startswith("git/ref/tags/"):
            return self.refs.get(path.removeprefix("git/ref/tags/"))
        assert path == "releases/tags/v0.2.1"
        return self.release if self.release and not self.release["draft"] else None

    def pages(self, path):
        assert path == "releases"
        return iter([self.release] if self.release is not None else [])

    def get(self, path):
        return self.tags[path.removeprefix("git/tags/")]

    def request(self, method, path, body):
        self.operations.append((method, path))
        if (method, path) == ("POST", "git/tags"):
            sha = f"tag-{len(self.tags)}"
            self.tags[sha] = {"tag": body["tag"], "message": body["message"],
                              "object": {"type": body["type"], "sha": body["object"]}}
            return {"sha": sha}
        if (method, path) == ("POST", "git/refs"):
            name = body["ref"].removeprefix("refs/tags/")
            self.refs[name] = {"object": {"type": "tag", "sha": body["sha"]}}
        elif method == "DELETE" and path.startswith("releases/"):
            self.release = None
        elif method == "DELETE" and path.startswith("git/refs/tags/"):
            del self.refs[path.removeprefix("git/refs/tags/")]
        else:
            raise AssertionError((method, path, body))

    def set_ref(self, name, sha):
        assert name == "stable"
        assert self.release is not None and not self.release["draft"]
        self.operations.append(("PROMOTE", name))
        if self.fail_promote:
            raise RuntimeError("promotion failed")
        self.refs[name] = {"object": {"type": "tag", "sha": sha}}

    def command(self, command):
        assert command[:2] == ["gh", "release"]
        self.operations.append((command[2], command[3]))
        if command[2] == "create":
            self.release = {"tag_name": "v0.2.1", "id": 123, "draft": True, "prerelease": False,
                            "assets": [{"name": asset.name} for asset in ASSETS]}
            if self.fail_upload:
                raise RuntimeError("upload failed")
        elif "--draft=false" in command:
            self.release["draft"] = False


class PublicationTests(unittest.TestCase):
    def test_stable_advances_only_after_complete_publication(self):
        api = FakeGitHub()
        publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual(api.refs["v0.2.1"], api.refs["stable"])
        self.assertLess(api.operations.index(("edit", "v0.2.1")), api.operations.index(("PROMOTE", "stable")))
        self.assertFalse(any(method == "DELETE" for method, _ in api.operations))

    def test_upload_failure_preserves_stable_and_releases_version_reservation(self):
        api = FakeGitHub()
        api.fail_upload = True
        with self.assertRaisesRegex(RuntimeError, "upload failed"):
            publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual("previous-stable", api.refs["stable"]["object"]["sha"])
        self.assertNotIn("v0.2.1", api.refs)
        self.assertIsNone(api.release)

    def test_failed_promotion_resumes_without_replacing_release_or_artifacts(self):
        api = FakeGitHub()
        api.fail_promote = True
        with self.assertRaisesRegex(RuntimeError, "promotion failed"):
            publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual("previous-stable", api.refs["stable"]["object"]["sha"])
        self.assertFalse(api.release["draft"])
        api.fail_promote = False
        api.operations.clear()
        publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual([("PROMOTE", "stable"), ("edit", "v0.2.1")], api.operations)

    def test_retry_cannot_rewind_an_intervening_stable_release(self):
        api = FakeGitHub()
        api.fail_promote = True
        with self.assertRaises(RuntimeError):
            publish_stable(api, PLAN, ASSETS, api.command)
        api.tags["newer-stable"] = {"tag": "v0.2.2", "message": json.dumps({"build": 1508})}
        api.refs["stable"] = {"object": {"type": "tag", "sha": "newer-stable"}}
        api.fail_promote = False
        api.operations.clear()
        with self.assertRaisesRegex(ValueError, "superseded"):
            publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual([], api.operations)
        self.assertEqual("newer-stable", api.refs["stable"]["object"]["sha"])

    def test_latest_rejects_stale_retries_but_accepts_new_builds_of_older_sources(self):
        class LatestApi:
            def optional(self, path):
                assert path == "releases/tags/main-latest"
                return {"id": 123}

        def download(command):
            assert command[:4] == ["gh", "release", "download", "main-latest"]
            with tarfile.open(Path(command[-1]) / "weavie-runner-linux-x64.tar.gz", "w:gz") as archive:
                payload = json.dumps({"buildNumber": 1508, "spawnContract": 2}).encode()
                info = tarfile.TarInfo("versions/1508/manifest.json")
                info.size = len(payload)
                archive.addfile(info, io.BytesIO(payload))

        for number in (1507, 1508):
            with self.subTest(number=number), self.assertRaisesRegex(ValueError, "supersedes"):
                ensure_latest_advances(LatestApi(), {**PLAN, "build": number}, download)
        ensure_latest_advances(LatestApi(), {**PLAN, "build": 1509, "commit": "old-source"}, download)

    def test_version_and_build_tags_cannot_be_reassigned(self):
        api = FakeGitHub()
        for name in ("v0.2.1", "build-1507"):
            immutable_tag(api, name, PLAN)
            with self.assertRaisesRegex(ValueError, "Refusing to replace"):
                immutable_tag(api, name, {**PLAN, "commit": "b" * 40})

    def test_existing_published_version_cannot_be_reused_by_another_build(self):
        api = FakeGitHub()
        publish_stable(api, PLAN, ASSETS, api.command)
        with self.assertRaisesRegex(ValueError, "immutable tag"):
            publish_stable(api, {**PLAN, "build": 1508}, ASSETS, api.command)

    def test_incomplete_published_release_is_not_promoted_or_deleted(self):
        api = FakeGitHub()
        immutable_tag(api, "v0.2.1", PLAN)
        api.release = {"tag_name": "v0.2.1", "id": 123, "draft": False, "prerelease": False, "assets": []}
        with self.assertRaisesRegex(ValueError, "complete stable release"):
            publish_stable(api, PLAN, ASSETS, api.command)
        self.assertEqual("previous-stable", api.refs["stable"]["object"]["sha"])
        self.assertFalse(api.release["draft"])


if __name__ == "__main__":
    unittest.main()
