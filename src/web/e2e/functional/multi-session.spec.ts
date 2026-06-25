import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { appliedEdit, endTurn } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Multiple sessions, each on its own worktree, working at the same time. The regression surface: a write or a
// review action in one session leaking into another's worktree, or the editor failing to rebind on a switch.
// Sessions/worktrees are a HostCore concern that differs structurally on remote (the Runner provisions the
// worktree), so these run @cross. Each session's writes are asserted against its own worktree on disk.

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

test.describe("a review reverted in one session leaves the other's worktree untouched", () => {
  const FILE = "{{WORKSPACE}}/hello.ts";
  // The full seed hello.ts with one line prepended → a single hunk, so a scope=change revert clears it.
  const APPLIED =
    "// ISO_MARKER inserted by claude\n" +
    "export function greet(name: string): string {\n" +
    "  return `Hello, ${name}!`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.log(message);\n";
  const sleep = { op: "sleep" as const, ms: 1500 };
  // Both sessions run the same fake, so each applies the edit to its OWN worktree's hello.ts.
  test.use({ fakeScript: { steps: [sleep, ...appliedEdit(FILE, APPLIED), endTurn()] } });

  test("reverting in session one does not revert session two @cross", async ({ page, weavie }) => {
    const chips = page.locator(".session-chip");

    // Session one's applied edit surfaces its review.
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("ISO_MARKER");

    // Fork: session two runs the same fake and applies the edit to its own worktree; wait for its review.
    await runCommand(page, "Fork Session");
    await expect(chips).toHaveCount(2);
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    const [worktreeTwo] = sessionWorktrees(weavie.workspace);
    expect(await readFile(join(worktreeTwo, "hello.ts"), "utf8")).toContain("ISO_MARKER");

    // Back on session one, revert the change — its worktree is restored.
    await chips.first().click();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });
    await page.locator(".weavie-inline-reject").click();
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).not.toContain("ISO_MARKER");

    // Session two's worktree is untouched by session one's revert.
    expect(await readFile(join(worktreeTwo, "hello.ts"), "utf8")).toContain("ISO_MARKER");
  });
});
