import type { Page } from "@playwright/test";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Click-to-edit write-back through the WHOLE stack (docs/specs/notion-writes.md): a click swaps a rendered block
// for a textarea holding its markdown source (line map → blockSource), Enter builds the exact-match op
// (buildUpdateOp) and posts source-save-edit → HostCore → the stubbed connector (WEAVIE_FAKE_NOTION), whose
// UpdateAsync enforces update_content's real must-match-once contract and round-trips the refreshed doc. The
// duplicate paragraphs below make that contract bite: editing the second only works if the web grew enough
// context around its old_str — a wrong or context-free op would conflict (or hit the wrong block).

const PAGE_URL = "https://www.notion.so/Editable-Doc-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d";

const NOTION_DOC = {
  title: "Editable Doc",
  editedTime: "2020-01-02T03:04:05.000Z",
  // Enhanced markdown AS THE API RETURNS IT: one block per line, tab-indented container children, trailing
  // block attrs — the same shapes the write path must keep byte-identical around an edit.
  markdown: [
    "Intro paragraph.",
    "Same twice.",
    'A colored line. {color="blue"}',
    '<callout icon="💡" color="green_bg">',
    "\tCallout body text.",
    "</callout>",
    "Same twice.",
  ].join("\n"),
};

async function openDoc(page: Page): Promise<ReturnType<Page["locator"]>> {
  await runCommand(page, "Open URL");
  const input = page.locator(".url-prompt-input");
  await expect(input).toBeVisible();
  await input.fill(PAGE_URL);
  await input.press("Enter");
  const source = page.locator(".editor-source");
  await expect(source.locator(".wv-title", { hasText: "Editable Doc" })).toBeVisible({
    timeout: 15_000,
  });
  return source;
}

test.describe("editing", () => {
  test.use({ notionDoc: NOTION_DOC });

  test("click → edit → Enter saves and re-renders, keeping the block's color", async ({ page }) => {
    const source = await openDoc(page);

    // The colored paragraph: its trailing {color="blue"} renders as a class, and the editor must show the
    // block's source WITHOUT the attr (it's re-attached invisibly on save).
    const colored = source.locator("p.wv-color-blue");
    await expect(colored).toHaveText("A colored line.");
    await colored.click();
    const editor = source.locator(".wv-block-editor");
    await expect(editor).toBeVisible();
    await expect(editor).toHaveValue("A colored line.");
    await expect(source.locator(".wv-edit-hint")).toBeVisible();

    await editor.fill("A recolored line.");
    await editor.press("Enter");

    // The save round-trips: the fake applies the op, the refreshed source-doc re-renders, the editor closes,
    // and the block keeps its color — formatting survived the edit.
    await expect(source.locator("p.wv-color-blue")).toHaveText("A recolored line.");
    await expect(source.locator(".wv-block-editor")).toHaveCount(0);
  });

  test("a callout child edits in place and keeps the callout's icon and color", async ({
    page,
  }) => {
    const source = await openDoc(page);

    await source.locator(".wv-callout p", { hasText: "Callout body text." }).click();
    const editor = source.locator(".wv-block-editor");
    // The child's source: no leading tab, no <callout> wrapper — just the block's own text.
    await expect(editor).toHaveValue("Callout body text.");
    await editor.fill("Callout body, edited.");
    await editor.press("Enter");

    const callout = source.locator(".wv-callout");
    await expect(callout.locator("p", { hasText: "Callout body, edited." })).toBeVisible();
    await expect(callout).toHaveClass(/wv-bg-green/);
    await expect(callout.locator(".wv-icon")).toHaveText("💡");
  });

  test("editing the second of two identical paragraphs changes only that one", async ({ page }) => {
    const source = await openDoc(page);

    // The stubbed UpdateAsync rejects any op that doesn't match exactly once, so this save succeeding IS the
    // proof that the web grew the old_str's context until it was unambiguous.
    await source.locator("p", { hasText: "Same twice." }).nth(1).click();
    const editor = source.locator(".wv-block-editor");
    await expect(editor).toHaveValue("Same twice.");
    await editor.fill("Same, but changed.");
    await editor.press("Enter");

    await expect(source.locator("p", { hasText: "Same, but changed." })).toBeVisible();
    await expect(source.locator("p", { hasText: "Same twice." })).toHaveCount(1); // the first is untouched
  });

  test("Escape cancels the edit and restores the block; Enter re-opens it from the keyboard", async ({
    page,
  }) => {
    const source = await openDoc(page);

    const intro = source.locator("p", { hasText: "Intro paragraph." });
    await intro.click();
    const editor = source.locator(".wv-block-editor");
    await editor.fill("A draft that must not survive.");
    await editor.press("Escape");

    await expect(source.locator(".wv-block-editor")).toHaveCount(0);
    await expect(intro).toBeVisible(); // the original text is back — nothing was saved

    // Cancel returned focus to the block, so plain Enter (the Edit Block command) re-opens the editor —
    // the whole loop is keyboard-reachable.
    await page.keyboard.press("Enter");
    await expect(source.locator(".wv-block-editor")).toHaveValue("Intro paragraph.");
  });
});

test.describe("stale edits", () => {
  test.use({ notionDoc: { ...NOTION_DOC, rejectEdits: true } });

  test("a conflicting save shows an inline stale error and Re-fetch restores the page", async ({
    page,
  }) => {
    const source = await openDoc(page);

    await source.locator("p", { hasText: "Intro paragraph." }).click();
    const editor = source.locator(".wv-block-editor");
    await editor.fill("This will conflict.");
    await editor.press("Enter");

    // The failure lands AT the block (not a toast): the editor re-enables with the reason and a re-fetch
    // escape hatch, because the page changed in Notion since it was fetched.
    const error = source.locator(".wv-edit-error");
    await expect(error).toContainText("changed in Notion");
    await expect(editor).toBeEnabled();

    await source.locator(".wv-edit-refetch").click();
    await expect(source.locator(".wv-block-editor")).toHaveCount(0);
    await expect(source.locator("p", { hasText: "Intro paragraph." })).toBeVisible({
      timeout: 15_000,
    });
  });
});

test.describe("truncated pages", () => {
  test.use({ notionDoc: { ...NOTION_DOC, truncated: true } });

  test("the incomplete banner shows and blocks stay editable", async ({ page }) => {
    const source = await openDoc(page);

    // The loss flags travel beside the markdown and render as a banner — targeted edits still work, because
    // update_content matches against the full page server-side.
    await expect(source.locator(".wv-incomplete")).toContainText("incomplete");
    await source.locator("p", { hasText: "Intro paragraph." }).click();
    await expect(source.locator(".wv-block-editor")).toHaveValue("Intro paragraph.");
  });
});
