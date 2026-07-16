import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { walkToChangedFile } from "../harness/navigator";

// The "diff against" journey: review the working tree against a ref through the same accept/reject inline-diff
// engine a turn uses — no forge, no new session, just git, with full Keep/Revert. See docs/specs/diff-against.md.

const git = (cwd: string, ...args: string[]): void => {
  execFileSync("git", args, { cwd, stdio: "ignore" });
};

const commitAll = (cwd: string, message: string): void => {
  git(cwd, "add", "-A");
  git(
    cwd,
    "-c",
    "user.email=e2e@example.com",
    "-c",
    "user.name=Weavie E2E",
    "-c",
    "commit.gpgsign=false",
    "commit",
    "-q",
    "-m",
    message,
  );
};

test("Diff Against HEAD reviews uncommitted changes with Keep/Revert", async ({ page, weavie }) => {
  // The suite's default 30s test timeout would otherwise cap this below the 30s toolbar wait itself.
  test.setTimeout(60_000);
  // An uncommitted edit to a seeded file — the exact thing "diff against HEAD" exists to review.
  const notes = join(weavie.workspace, "notes.txt");
  await writeFile(notes, "just plain text\nplus an uncommitted line\n");

  await runCommand(page, "Diff Against HEAD");

  // The navigator surfaces on the changed file, labeled with what it's diffing against.
  const toolbar = page.locator(".weavie-inline-toolbar");
  await expect(toolbar).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".weavie-inline-stack-name")).toHaveText("notes.txt");
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText("vs HEAD");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();

  // Not read-only any more: Keep / Revert act on the diff (the whole point of the unification). Comment stays
  // absent — a local ref has no forge behind it.
  await expect(toolbar.locator(".weavie-inline-accept")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-reject")).toBeVisible();
  await expect(toolbar.locator(".weavie-inline-comment")).toHaveCount(0);

  // Reject the change → the uncommitted line is backed out on disk (the file returns to its committed content)
  // and its added marker clears, exactly as a turn revert.
  await toolbar.locator(".weavie-inline-reject").click();
  await expect(page.locator(".weavie-inline-added")).toHaveCount(0, { timeout: 10_000 });
});

test("Diff Against… prompts for a ref and walks a multi-file diff", async ({ page, weavie }) => {
  // The suite's default 30s test timeout would otherwise cap this below the 30s toolbar wait itself.
  test.setTimeout(60_000);
  // A second commit changing two files, so diffing against the first commit is a two-file walk.
  await writeFile(
    join(weavie.workspace, "hello.ts"),
    "export function greet(name: string): string {\n" +
      "  return `Howdy, ${name}!`;\n" +
      "}\n\n" +
      'const message = greet("weavie");\n' +
      "console.log(message);\n",
  );
  await writeFile(join(weavie.workspace, "feature.ts"), "export const feature = true;\n");
  commitAll(weavie.workspace, "second");

  await runCommand(page, "Diff Against…");

  // The prompt takes any commit-ish; HEAD^ is the seed commit here.
  const prompt = page.locator(".session-prompt");
  await expect(prompt).toBeVisible();
  await prompt.locator(".session-prompt-input").fill("HEAD^");
  await prompt.locator(".session-prompt-input").press("Enter");

  // The navigator arms on the first changed file and ← / → walks to the other.
  await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".weavie-inline-stack-sub")).toContainText("vs HEAD^");
  await walkToChangedFile(page, "hello.ts");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
});

test("Diff Against… typeahead surfaces remote-tracking branches", async ({ page, weavie }) => {
  test.setTimeout(60_000);
  // A remote-tracking branch, without a live remote — the state a fetch leaves behind. Before the fix the
  // typeahead listed only local heads, so origin/* refs (the usual diff base) never appeared.
  const head = execFileSync("git", ["rev-parse", "HEAD"], { cwd: weavie.workspace })
    .toString()
    .trim();
  git(weavie.workspace, "update-ref", "refs/remotes/origin/release", head);

  await runCommand(page, "Diff Against…");

  const prompt = page.locator(".session-prompt");
  await expect(prompt).toBeVisible();
  await prompt.locator(".session-prompt-input").fill("origin");
  await expect(
    prompt.locator(".session-prompt-suggestion", { hasText: "origin/release" }),
  ).toBeVisible({ timeout: 10_000 });
});

test("a ref with no changes answers with a toast, not an empty navigator", async ({
  page,
  weavie,
}) => {
  // The tree is clean (the seed commit), so there is nothing to review against HEAD.
  void weavie;
  // The suite's default 30s test timeout would otherwise cap this below the 30s toast wait itself.
  test.setTimeout(60_000);
  await runCommand(page, "Diff Against HEAD");

  await expect(page.locator(".toast", { hasText: "No changes against 'HEAD'" })).toBeVisible({
    timeout: 30_000,
  });
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
});
