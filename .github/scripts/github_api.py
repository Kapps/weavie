"""GitHub release automation transport; only 404 means an optional resource is absent."""

import json
import os
import urllib.error
import urllib.request


class GitHub:
    def __init__(self):
        self.root = f"{os.environ['GITHUB_API_URL']}/repos/{os.environ['GITHUB_REPOSITORY']}"
        self.token = os.environ["GH_TOKEN"]

    def request(self, method, path, body):
        data = None if body is None else json.dumps(body).encode()
        request = urllib.request.Request(
            f"{self.root}/{path}",
            data=data,
            method=method,
            headers={
                "Authorization": f"Bearer {self.token}",
                "Accept": "application/vnd.github+json",
                "Content-Type": "application/json",
                "X-GitHub-Api-Version": "2022-11-28",
            },
        )
        with urllib.request.urlopen(request) as response:
            raw = response.read()
            return json.loads(raw) if raw else None

    def get(self, path):
        return self.request("GET", path, None)

    def optional(self, path):
        try:
            return self.get(path)
        except urllib.error.HTTPError as error:
            if error.code != 404:
                raise
            return None

    def pages(self, path):
        page = 1
        while True:
            items = self.get(f"{path}?per_page=100&page={page}")
            yield from items
            if len(items) < 100:
                return
            page += 1

    def set_ref(self, name, sha):
        if self.optional(f"git/ref/tags/{name}") is None:
            self.request("POST", "git/refs", {"ref": f"refs/tags/{name}", "sha": sha})
        else:
            self.request("PATCH", f"git/refs/tags/{name}", {"sha": sha, "force": True})
