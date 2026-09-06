from pathlib import Path
import subprocess
import tempfile
import unittest

from checkout_release_source import checkout_source


class ReleaseCheckoutTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory()
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.git("init", "--initial-branch=main")
        self.git("config", "user.name", "Release test")
        self.git("config", "user.email", "release@example.test")
        (self.root / "trusted").write_text("main")
        self.git("add", ".")
        self.git("commit", "-m", "trusted")
        self.main = self.git("rev-parse", "HEAD")
        self.git("update-ref", "refs/remotes/origin/main", self.main)
        self.git("checkout", "-b", "untrusted-pr")
        (self.root / "malicious-build-script").write_text("execute arbitrary PR code")
        self.git("add", ".")
        self.git("commit", "-m", "unmerged PR")
        self.pr = self.git("rev-parse", "HEAD")
        self.git("checkout", "main")

    def git(self, *args):
        return subprocess.check_output(["git", *args], cwd=self.root, text=True,
                                       stderr=subprocess.DEVNULL).strip()

    def test_unmerged_pr_is_rejected_without_checking_out_its_files(self):
        with self.assertRaisesRegex(ValueError, "not in trusted main history"):
            checkout_source(self.root, self.pr)
        self.assertEqual(self.main, self.git("rev-parse", "HEAD"))
        self.assertFalse((self.root / "malicious-build-script").exists())

    def test_branch_names_and_shell_payloads_are_rejected_before_checkout(self):
        for source in ("untrusted-pr", self.pr[:7], "HEAD", "$(touch compromised)"):
            with self.subTest(source=source), self.assertRaisesRegex(ValueError, "full 40-character"):
                checkout_source(self.root, source)
        self.assertEqual(self.main, self.git("rev-parse", "HEAD"))

    def test_main_ancestor_is_accepted_without_executing_checkout_hooks(self):
        (self.root / "second").write_text("also trusted")
        self.git("add", ".")
        self.git("commit", "-m", "second")
        self.git("update-ref", "refs/remotes/origin/main", self.git("rev-parse", "HEAD"))
        hooks = self.root / ".git" / "hooks"
        hook = hooks / "post-checkout"
        hook.write_text("#!/bin/sh\ntouch compromised\n")
        hook.chmod(0o755)
        checkout_source(self.root, self.main)
        self.assertEqual(self.main, self.git("rev-parse", "HEAD"))
        self.assertFalse((self.root / "compromised").exists())


if __name__ == "__main__":
    unittest.main()
