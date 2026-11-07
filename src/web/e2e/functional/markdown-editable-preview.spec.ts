import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { clickIntoEditor, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ markdownView: "preview" });

const preview = (page: Page) => page.locator(".editor-preview-body");
const input = (page: Page) => page.locator(".wv-preview-block-input");

// Markdown opens rendered; a clicked block shows its source in place and writes straight to the working copy.
test("markdown opens as a preview whose blocks edit in place", async ({ page }) => {
  await openFile(page, "README.md");
  await expect(preview(page).locator("strong")).toHaveText("world");

  await preview(page).locator("strong").click();
  await expect(input(page)).toBeFocused();
  await expect(input(page)).toHaveValue(/\*\*world\*\*/);
  await input(page).press("End");
  await input(page).pressSequentially(" and *more*");
  await page.keyboard.press("Escape");

  await expect(input(page)).toBeHidden();
  await expect(preview(page).locator("em", { hasText: "more" })).toBeVisible();

  await page.locator(".editor-preview-toggle").click();
  await expect(page.locator(".view-lines")).toContainText("and *more*");
});

// A diagram swaps to its text on click and re-renders from the edited source.
test("a mermaid diagram edits as text and re-renders", async ({ page }) => {
  await openFile(page, "README.md");
  const diagram = preview(page).locator(".mermaid-rendered > svg");
  await expect(diagram).toBeVisible({ timeout: 15_000 });

  await diagram.click();
  await expect(input(page)).toHaveValue(/```mermaid/);
  await expect(diagram).toBeHidden();
  await page.keyboard.press("Escape");
  await expect(diagram).toBeVisible();
});

// Enter at the end of a block continues below it; empty blocks leave no trace.
test("enter adds a block and an abandoned one disappears", async ({ page }) => {
  await openFile(page, "README.md");
  const heading = preview(page).locator("h1").first();
  await heading.click();
  await input(page).press("End");
  await page.keyboard.press("Enter");
  await expect(input(page)).toHaveValue("");
  await page.keyboard.type("Fresh paragraph");
  await page.keyboard.press("Enter");
  await expect(input(page)).toHaveValue("");
  await page.keyboard.press("Escape");

  await expect(preview(page).locator("p", { hasText: "Fresh paragraph" })).toBeVisible();
  await expect(input(page)).toBeHidden();
});

// The block edit is one undo step on the same working copy, reachable from the rendered view.
test("undo from the preview reverts a block edit", async ({ page }) => {
  await openFile(page, "README.md");
  await preview(page).locator("strong").click();
  await input(page).press("End");
  await input(page).pressSequentially(" undone");
  await page.keyboard.press("Escape");
  await expect(preview(page)).toContainText("undone");

  await page.keyboard.press("ControlOrMeta+z");
  await expect(preview(page)).not.toContainText("undone");
  await expect(preview(page).locator("strong")).toHaveText("world");
});

// The Source toggle is per-file and wins over the default.
test("toggling to source shows Monaco for that file", async ({ page }) => {
  await openFile(page, "README.md");
  await expect(preview(page)).toBeVisible();
  await page.locator(".editor-preview-toggle").click();
  await clickIntoEditor(page);
  await expect(preview(page)).toBeHidden();
});

// Removing a new item keeps the blank line that separates the list from what follows it.
test("an abandoned list item leaves the list and next paragraph apart", async ({ page }) => {
  await openFile(page, "tasks.md");
  await preview(page).locator("li", { hasText: "third" }).click();
  await input(page).press("End");
  await page.keyboard.press("Enter");
  await expect(input(page)).toHaveValue("- ");
  await page.keyboard.press("Enter");
  await expect(input(page)).toHaveValue("");
  await page.keyboard.type("After the list");
  await page.keyboard.press("Escape");
  await expect(preview(page).locator("li")).toHaveCount(3);
  await expect(preview(page).locator("p", { hasText: "After the list" })).toBeVisible();
});

// Document HTML posing as an editable block can't reach the file through a forged line range.
test("raw HTML can't forge an editable block", async ({ page, weavie }) => {
  const path = join(weavie.workspace, "forged.md");
  const text = 'Real\n\n<div class="md-block" data-md-start="0" data-md-end="9">Forged</div>\n';
  await writeFile(path, text);
  await openFile(page, "forged.md");
  await preview(page).getByText("Forged").click();
  await expect(input(page)).toHaveValue(/<div class="md-block"/);
  await page.keyboard.press("Escape");
  await expect(input(page)).toBeHidden();
  expect(await readFile(path, "utf8")).toBe(text);
});

// A link to a line lands on that block in the rendered document, not on Monaco underneath.
test("a line-targeted open reveals the block in the preview", async ({ page, weavie }) => {
  const filler = Array.from({ length: 80 }, (_, index) => `Paragraph ${index}`).join("\n\n");
  await writeFile(join(weavie.workspace, "long.md"), `${filler}\n\n## Target\n`);
  await writeFile(join(weavie.workspace, "jump.md"), "[go](./long.md:161)\n");
  await openFile(page, "jump.md");
  await preview(page).getByRole("link", { name: "go" }).click();
  await expect(preview(page).getByRole("heading", { name: "Target" })).toBeInViewport();
});

// A list item edits alone, Enter continues the list, and the arrows walk between blocks.
test("list items edit individually and arrows move between blocks", async ({ page }) => {
  await openFile(page, "tasks.md");
  await preview(page).locator("li", { hasText: "second" }).click();
  await expect(input(page)).toHaveValue("- second");
  await input(page).press("End");
  await page.keyboard.press("Enter");
  await expect(input(page)).toHaveValue("- ");
  await page.keyboard.type("inserted");
  await page.keyboard.press("ArrowDown");
  await expect(input(page)).toHaveValue("- third");
  await page.keyboard.press("Home");
  await page.keyboard.press("ArrowUp");
  await expect(input(page)).toHaveValue("- inserted");
  await page.keyboard.press("Escape");
  await expect(preview(page).locator("li")).toHaveText(["first", "second", "inserted", "third"]);
});

// Another writer moving the block keeps the open editor attached; rewriting the block itself closes it loudly.
test("an open block follows outside edits and yields when overwritten", async ({
  page,
  weavie,
}) => {
  await openFile(page, "tasks.md");
  await preview(page).locator("li", { hasText: "second" }).click();
  await expect(input(page)).toHaveValue("- second");

  const path = join(weavie.workspace, "tasks.md");
  await writeFile(path, "# Added\n\n- first\n- second\n- third\n");
  await expect(preview(page).locator("h1", { hasText: "Added" })).toBeVisible();
  await expect(input(page)).toBeFocused();
  await input(page).press("End");
  await input(page).pressSequentially("!");
  await expect.poll(() => readFile(path, "utf8")).toContain("- second!\n");

  await writeFile(path, "# Added\n\nAll replaced\n");
  await expect(page.getByText("changed outside the preview")).toBeVisible();
  await expect(input(page)).toBeHidden();
  await expect(preview(page)).toContainText("All replaced");
});
