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

  // Reading shows bars and a removal chip; Next Change marks one change's words; Escape returns it to a bar.
  test("reads as bars and steps through changes in place", async ({ page }) => {
    await openPlan(page);
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(2);
    await expect(preview(page).locator(".wv-md-added")).toHaveCount(2);
    await expect(preview(page).locator(".wv-md-removed")).toContainText("Events land in a queue.");
    await expect(preview(page).locator(".wv-md-ins")).toHaveCount(0);

    await page.locator(".editor-preview").focus();
    await page.keyboard.press(navChord("ArrowDown"));
    await expect(preview(page).locator(".wv-md-ins")).toContainText(["weekly"]);
    await expect(preview(page).locator(".wv-md-del")).toContainText(["daily"]);
    await page.keyboard.press(navChord("ArrowDown"));
    await expect(preview(page).locator(".wv-md-removed-body")).toContainText("Events land");
    await expect(preview(page).locator(".wv-md-ins")).toHaveCount(0);
    await page.keyboard.press("Escape");
    await expect(preview(page).locator(".wv-md-removed-body")).toHaveCount(0);
  });

  // Keep and Revert act only on the change on screen: the first Keep shows it, a bar click shows another.
  test("keeps and reverts only the change on screen", async ({ page, weavie }) => {
    await openPlan(page);
    await page.locator(".editor-preview").focus();
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(preview(page).locator(".wv-md-ins").first()).toBeVisible();
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(2);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(1);

    const item = preview(page).locator("li", { hasText: "reworded" });
    await item.locator(".wv-md-bar").click();
    await expect(item.locator(".wv-md-ins")).toContainText(", reworded");
    await page.keyboard.press("ControlOrMeta+Backspace");
    const path = join(weavie.workspace, "plan.md");
    await expect.poll(() => readFile(path, "utf8")).toContain("- second\n- third");
    expect(await readFile(path, "utf8")).toContain("## Rollout");
  });

  // Changes only folds untouched blocks behind a divider; editing a changed block offers the agent's original.
  test("folds unchanged blocks and shows the original while editing", async ({ page }) => {
    await openPlan(page);
    const toggle = page.locator(".weavie-inline-changes-only");
    await toggle.click();
    await expect(toggle).toHaveAttribute("aria-pressed", "true");
    const fold = preview(page).locator(".wv-md-fold").first();
    await expect(fold).toContainText("unchanged blocks");
    await fold.getByRole("button", { name: "Show" }).click();
    await expect(preview(page).locator(".wv-md-folded")).toHaveCount(0);

    await preview(page).getByText("batched at 9am").click();
    const box = page.locator(".wv-preview-block-box");
    await expect(box).toContainText("Changed by agent");
    await box.getByRole("button", { name: "Show original" }).click();
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

    // Scrolled away from the shown change, Keep brings it back into view instead of acting out of sight.
    await page.locator(".editor-preview").evaluate((pane) => pane.scrollTo({ top: 0 }));
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(preview(page).locator(".wv-md-ins")).toBeInViewport();
    await expect(preview(page).locator(".wv-md-changed")).toHaveCount(2);

    await page.keyboard.press("ControlOrMeta+Backspace");
    const path = join(weavie.workspace, "plan.md");
    await expect.poll(() => readFile(path, "utf8")).toContain("Paragraph 58.");
    expect(await readFile(path, "utf8")).toContain("Paragraph one.");
  });
});
