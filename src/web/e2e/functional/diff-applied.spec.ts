import { existsSync } from "node:fs";
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { appliedEdit, endTurn } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";

// The post-turn "applied" review — the surface for edits Claude has already written to disk (every permission
// mode records them through the hook stream). Distinct from diff.spec.ts, which covers the blocking openDiff
// "review" proposal. Here the change tracker folds a PreToolUse→write→PostToolUse beat into the review set,
// and a Stop hook (Idle) arms the auto-open. This is the heart of "diff management": Keep advances the review
// baseline without touching disk; Revert restores the file on disk from Core's baseline.

const FILE = "{{WORKSPACE}}/hello.ts";
// One added line at the top of the seed hello.ts → a single hunk to keep/revert.
const APPLIED =
  "// APPLIED_MARKER inserted by claude\n" +
  "export function greet(name: string): string {\n" +
  "  return `Hello, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.log(message);\n";

// A second changed file, so a turn's review spans two files — the "All files" scope only appears for a
// multi-file review.
const FILE_B = "{{WORKSPACE}}/notes.txt";
const APPLIED_B = "// NOTES_MARKER added by claude\njust plain text\n";

// A file that does NOT exist at baseline, so reverting its only hunk must delete it (not leave a 0-byte file).
const CREATED = "{{WORKSPACE}}/created.ts";

const sleep = { op: "sleep" as const, ms: 1500 };

function appliedTurn() {
  return { steps: [sleep, ...appliedEdit(FILE, APPLIED), endTurn()] };
}

function twoFileTurn() {
  return {
    steps: [sleep, ...appliedEdit(FILE, APPLIED), ...appliedEdit(FILE_B, APPLIED_B), endTurn()],
  };
}

test.describe("applied review surfaces", () => {
  test.use({ fakeScript: appliedTurn() });

  test("an applied edit auto-opens the post-turn review with the scope toolbar", async ({
    page,
  }) => {
    // The applied toolbar (unlike the openDiff proposal) carries the scope picker.
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    await expect(page.locator(".weavie-inline-scope")).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("APPLIED_MARKER");
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
  });
});

test.describe("keep a change", () => {
  test.use({ fakeScript: appliedTurn() });

  test("keeping a change clears the marker and leaves the edit on disk", async ({
    page,
    weavie,
  }) => {
    const keep = page.locator(".weavie-inline-accept");
    await expect(keep).toBeVisible({ timeout: 20_000 });
    await keep.click();

    // Keep advances the review baseline (no disk write): the toolbar clears, the edit stays on disk.
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    const onDisk = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
    expect(onDisk).toContain("APPLIED_MARKER");
  });
});

test.describe("revert a change", () => {
  test.use({ fakeScript: appliedTurn() });

  test("reverting a change restores the file on disk @cross", async ({ page, weavie }) => {
    const revert = page.locator(".weavie-inline-reject");
    await expect(revert).toBeVisible({ timeout: 20_000 });

    // Sanity: the edit is on disk before the revert.
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("APPLIED_MARKER");

    await revert.click();

    // Revert writes Core's baseline back to disk: the marker is gone, the original is restored, toolbar clears.
    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    const onDisk = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
    expect(onDisk).not.toContain("APPLIED_MARKER");
    expect(onDisk).toContain("greet");
  });
});

test.describe("revert all via the scope picker", () => {
  test.use({ fakeScript: twoFileTurn() });

  test("revert-all confirms then restores every file on disk", async ({ page, weavie }) => {
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    // Both files are on disk with their edits before the revert.
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("APPLIED_MARKER");
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toContain("NOTES_MARKER");

    // Switch the scope to "All files" (only offered for a multi-file review), then Revert → undo-the-whole-set
    // (a confirmed, destructive action).
    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "All files" }).click();
    await page.locator(".weavie-inline-reject").click();

    const dialog = page.locator(".confirm-dialog");
    await expect(dialog).toBeVisible();
    await dialog.locator(".confirm-btn-primary").click();

    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).not.toContain(
      "APPLIED_MARKER",
    );
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).not.toContain(
      "NOTES_MARKER",
    );
  });
});

test.describe("revert a whole file via the scope picker", () => {
  test.use({ fakeScript: appliedTurn() });

  test("revert-file confirms then restores the file on disk @cross", async ({ page, weavie }) => {
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("APPLIED_MARKER");

    // Scope = "This file", then Revert → reset the whole file to its baseline (a confirmed action).
    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
    await page.locator(".weavie-inline-reject").click();

    const dialog = page.locator(".confirm-dialog");
    await expect(dialog).toBeVisible();
    await dialog.locator(".confirm-btn-primary").click();

    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    const onDisk = await readFile(join(weavie.workspace, "hello.ts"), "utf8");
    expect(onDisk).not.toContain("APPLIED_MARKER");
    expect(onDisk).toContain("greet");
  });
});

test.describe("revert a created file", () => {
  test.use({
    fakeScript: {
      steps: [sleep, ...appliedEdit(CREATED, "export const created = true;\n"), endTurn()],
    },
  });

  test("reverting a file that didn't exist at baseline deletes it from disk", async ({
    page,
    weavie,
  }) => {
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 20_000 });
    expect(existsSync(join(weavie.workspace, "created.ts"))).toBe(true);

    // The whole file is one added hunk; reverting it returns the file to non-existence, not a 0-byte file.
    await page.locator(".weavie-inline-reject").click();

    await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
    expect(existsSync(join(weavie.workspace, "created.ts"))).toBe(false);
  });
});
