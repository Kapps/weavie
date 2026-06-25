import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Multiple sessions, each on its own worktree, working at the same time. The regression surface: a write in one
// session leaking into another's worktree, or the editor failing to rebind on a switch. Sessions/worktrees are a
// HostCore concern that differs structurally on remote (the Runner provisions the worktree), so this runs
// @cross. Each session's writes are asserted against its own worktree on disk. (Diff-review behavior across a
// switch is covered by diff.spec.ts "a session keeps its diff across a switch"; the applied-review surface by
// diff-review.spec.ts.)

async function saveActiveEditor(page: import("@playwright/test").Page): Promise<void> {
  // The dirty marker clearing is the flush-to-disk signal — never a fixed sleep.
  await page.keyboard.press("ControlOrMeta+s");
  await expect(page.locator(".editor-tab .editor-tab-dirty")).toHaveCount(0);
}

test("concurrent edits land in each session's own worktree and survive a switch @cross", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");

  // Session one edits hello.ts in the primary worktree.
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.type("SESSIONONEMARKER");
  await saveActiveEditor(page);

  // Fork a second session (its own worktree) and edit the same-named file there.
  await runCommand(page, "Fork Session");
  await expect(chips).toHaveCount(2);
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.type("SESSIONTWOMARKER");
  await saveActiveEditor(page);

  // Each write landed in its own worktree, with no cross-contamination.
  const [worktreeTwo] = sessionWorktrees(weavie.workspace);
  expect(worktreeTwo).toBeTruthy();
  const primaryFile = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
  const forkedFile = await readFile(join(worktreeTwo, "hello.ts"), "utf8");
  expect(primaryFile).toContain("SESSIONONEMARKER");
  expect(primaryFile).not.toContain("SESSIONTWOMARKER");
  expect(forkedFile).toContain("SESSIONTWOMARKER");
  expect(forkedFile).not.toContain("SESSIONONEMARKER");

  // Switching back rebinds the editor to session one's worktree — its own edit, not the fork's.
  await chips.first().click();
  const viewLines = page.locator(".monaco-editor .view-lines");
  await expect(viewLines).toContainText("SESSIONONEMARKER", { timeout: 15_000 });
  await expect(viewLines).not.toContainText("SESSIONTWOMARKER");
});
