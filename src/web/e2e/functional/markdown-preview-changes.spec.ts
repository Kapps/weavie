import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { navChord } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

test.use({ markdownView: "preview" });

const PLAN = [
  "# Plan",
  "",
  "Send one daily digest per user.",
  "",
  "## Design",
  "",
  "Events land in a queue.",
  "",
  "## Notes",
  "",
  "- first",
  "- second",
  "- third",
  "",
  "## Later",
  "",
  "Nothing yet.",
  "",
].join("\n");

const seedPlan = { run: (workspace: string) => writeFile(join(workspace, "plan.md"), PLAN) };
const preview = (page: Page) => page.locator(".editor-preview-body");

// The agent's turn rewords one paragraph, adds a section, rewords a list item, and drops a paragraph.
const edited = PLAN.replace("daily digest", "weekly digest, batched at 9am,")
  .replace("- second", "- second, reworded")
  .replace("Events land in a queue.\n\n", "")
  .replace("## Later", "## Rollout\n\nStaff first.\n\n## Later");

async function openPlan(page: Page): Promise<void> {
  await expect(page.locator(".editor-empty-review")).toBeVisible({ timeout: 20_000 });
  await openFile(page, "plan.md");
  await expect(preview(page)).toContainText("weekly digest");
  await expect(preview(page).locator(".wv-md-bar").first()).toBeVisible();
}

test.describe("pending agent changes in a rendered Markdown file", () => {
  test.use({
    workspaceSeed: seedPlan,
    fakeScript: { steps: appliedEdit("plan.md", edited) },
  });

  // Reading shows one bar per changed block and a notch for the removal, with no word marks yet.
  test("reads as margin bars and a removal notch", async ({ page }) => {
    await openPlan(page);
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(2);
    await expect(preview(page).locator(".wv-md-added")).toHaveCount(2);
    await expect(preview(page).locator(".wv-md-removed")).toContainText("Events land in a queue.");
    await expect(preview(page).locator(".wv-md-ins")).toHaveCount(0);
    await expect(page.locator(".weavie-inline-stack-sub")).toContainText("/4");
  });

  // Next Change shows one change's words in the rendered text; Escape returns it to a bar.
  test("steps through changes in the rendered view", async ({ page }) => {
    await openPlan(page);
    await page.locator(".editor-preview").focus();
    await page.keyboard.press(navChord("ArrowDown"));

    await expect(preview(page)).toBeVisible();
    await expect(preview(page).locator(".wv-md-ins")).toContainText(["weekly"]);
    await expect(preview(page).locator(".wv-md-del")).toContainText(["daily"]);

    await page.keyboard.press(navChord("ArrowDown"));
    await expect(preview(page).locator(".wv-md-removed-body")).toContainText("Events land");
    await expect(preview(page).locator(".wv-md-ins")).toHaveCount(0);

    await page.keyboard.press("Escape");
    await expect(preview(page).locator(".wv-md-removed-body")).toHaveCount(0);
  });

  // Clicking a bar shows that change, and Revert restores exactly that list item on disk.
  test("reverts one list item from its bar", async ({ page, weavie }) => {
    await openPlan(page);
    const item = preview(page).locator("li", { hasText: "reworded" });
    await item.locator(".wv-md-bar").click();
    await expect(item.locator(".wv-md-ins")).toContainText(", reworded");

    await page.keyboard.press("ControlOrMeta+Backspace");

    const path = join(weavie.workspace, "plan.md");
    await expect.poll(() => readFile(path, "utf8")).toContain("- second\n- third");
    const disk = await readFile(path, "utf8");
    expect(disk).toContain("weekly digest");
    expect(disk).toContain("## Rollout");
  });

  // Keep never acts on a change you haven't seen: the first press shows it, the second keeps it.
  test("keeps only a change that is on screen", async ({ page }) => {
    await openPlan(page);
    await page.locator(".editor-preview").focus();
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(preview(page).locator(".wv-md-ins").first()).toBeVisible();
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(2);

    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(1);
  });

  // Changes only folds the untouched blocks behind a row that brings them back.
  test("folds unchanged blocks on request", async ({ page }) => {
    await openPlan(page);
    await page.locator(".weavie-inline-changes-only").click();
    await expect(page.locator(".weavie-inline-changes-only")).toHaveAttribute(
      "aria-pressed",
      "true",
    );
    const fold = preview(page).locator(".wv-md-fold").first();
    await expect(fold).toContainText("unchanged blocks");
    await fold.getByRole("button", { name: "Show" }).click();
    await expect(preview(page).locator(".wv-md-folded")).toHaveCount(0);
  });

  // Editing a changed block offers the agent's original inside the same box.
  test("shows the original while editing a changed block", async ({ page }) => {
    await openPlan(page);
    await preview(page).getByText("batched at 9am").click();
    const box = page.locator(".wv-preview-block-box");
    await expect(box).toContainText("Changed by agent");
    await box.getByRole("button", { name: "Show original" }).click();
    await expect(box.locator(".wv-preview-block-original")).toContainText("daily digest");
    await expect(box.locator(".wv-preview-block-original .wv-md-del")).toContainText("daily");
  });
});

test.describe("a mostly rewritten Markdown file", () => {
  test.use({
    workspaceSeed: seedPlan,
    fakeScript: { steps: appliedEdit("plan.md", "# Other\n\nAll new.\n\n- a\n- b\n") },
  });

  // Marking nearly every block means nothing, so one banner offers the line diff instead.
  test("shows one banner instead of per-block marks", async ({ page }) => {
    await expect(page.locator(".editor-empty-review")).toBeVisible({ timeout: 20_000 });
    await openFile(page, "plan.md");
    await expect(page.locator(".wv-md-banner")).toContainText("Mostly rewritten");
    await expect(preview(page).locator(".wv-md-bar")).toHaveCount(0);
    await page
      .locator(".wv-md-banner")
      .getByRole("button", { name: "Show the diff in Source" })
      .click();
    await expect(preview(page)).toBeHidden();
    await expect(page.locator(".weavie-inline-added").first()).toBeVisible();
  });
});

test.describe("every change marked at once", () => {
  test.use({
    workspaceSeed: seedPlan,
    fakeScript: {
      steps: [
        {
          op: "mcp",
          tool: "setSetting",
          args: { key: "editor.markdownChangeMarks", value: "all" },
        },
        ...appliedEdit("plan.md", edited),
      ],
    },
  });

  // The setting marks the words of every changed block without stepping to them.
  test("marks every changed block's words", async ({ page }) => {
    await openPlan(page);
    await expect(preview(page).locator(".wv-md-ins")).toContainText(["weekly", ", reworded"]);
  });
});

const LONG = Array.from({ length: 60 }, (_, index) => `Paragraph ${index}.`).join("\n\n");

test.describe("changes in a document taller than the pane", () => {
  test.use({
    workspaceSeed: {
      run: (workspace: string) => writeFile(join(workspace, "plan.md"), `${LONG}\n`),
    },
    fakeScript: {
      steps: appliedEdit(
        "plan.md",
        `${LONG.replace("Paragraph 1.", "Paragraph one.").replace("Paragraph 58.", "Paragraph fifty-eight.")}\n`,
      ),
    },
  });

  // The review position is the change on screen, so Next walks on and Keep keeps that change, far down or not.
  test("steps and keeps by the change on screen", async ({ page, weavie }) => {
    await expect(page.locator(".editor-empty-review")).toBeVisible({ timeout: 20_000 });
    await openFile(page, "plan.md");
    await expect(preview(page).locator(".wv-md-bar")).toHaveCount(2);
    await page.locator(".editor-preview").focus();

    await page.keyboard.press(navChord("ArrowDown"));
    await expect(preview(page).locator(".wv-md-ins")).toContainText(["one"]);
    await page.keyboard.press(navChord("ArrowDown"));
    await expect(preview(page).locator(".wv-md-ins")).toContainText(["fifty-eight"]);
    await expect(preview(page).locator(".wv-md-ins")).toBeInViewport();

    await page.keyboard.press("ControlOrMeta+Backspace");
    const path = join(weavie.workspace, "plan.md");
    await expect.poll(() => readFile(path, "utf8")).toContain("Paragraph 58.");
    expect(await readFile(path, "utf8")).toContain("Paragraph one.");
  });
});
