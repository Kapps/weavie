import { access, readFile, unlink } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { openFile, pressDocumentStart, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const notesContent = "a changed note\nkeep this second line\n";
const helloContent = "// selected greeting\nexport const greeting = 'hello';\n";
const sectionFor = (page: Page, name: string): Locator =>
  page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: name }),
  });

async function selectFirstLine(page: Page, section: Locator): Promise<void> {
  await section
    .locator(".view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
  await pressDocumentStart(page);
  await page.keyboard.press("Shift+End");
}

async function submitRevision(page: Page): Promise<void> {
  const prompt = page.locator(".session-prompt-input");
  await expect(prompt).toBeFocused();
  await prompt.fill("Shorten this comment to one line");
  await prompt.press("Enter");
}

test.describe("unified review editor interactions", () => {
  test.use({
    inference: "success",
    automaticInference: true,
    fakeScript: {
      steps: [...appliedEdit("hello.ts", helloContent), ...appliedEdit("notes.txt", notesContent)],
    },
  });

  test("revision and search follow the selected section with no open tabs", async ({
    page,
    weavie,
  }) => {
    await awaitReviewSet(page, ["hello.ts", "notes.txt"]);
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".editor-tab")).toHaveCount(0);
    const notes = sectionFor(page, "notes.txt");
    await selectFirstLine(page, notes);
    await expect(page.locator(".editor-surface .pane-footer")).toContainText(
      "Ln 1, Col 15 (14 selected)",
    );
    await page.keyboard.press("ControlOrMeta+Shift+f");
    await expect(page.locator(".search-input")).toHaveValue("a changed note");
    await page.keyboard.press("Escape");
    await selectFirstLine(page, notes);
    await page.keyboard.press("ControlOrMeta+Alt+e");
    await submitRevision(page);
    await expect
      .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
      .toBe("// revised by the fake\nkeep this second line\n");
    await expect(notes.locator(".view-lines")).toContainText("// revised by the fake");

    const hello = sectionFor(page, "hello.ts");
    await selectFirstLine(page, hello);
    await page.keyboard.press("ControlOrMeta+Alt+e");
    await submitRevision(page);
    await expect
      .poll(() => readFile(join(weavie.workspace, "hello.ts"), "utf8"))
      .toBe("// revised by the fake\nexport const greeting = 'hello';\n");
    await expect(page.locator(".editor-tab")).toHaveCount(0);
    await expect(page.locator(".weavie-revising-pill")).toHaveCount(0);
  });

  test("right-click revision and Cut retain their section when a different tab is hidden", async ({
    page,
    weavie,
  }) => {
    await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
    await awaitReviewSet(page, ["hello.ts", "notes.txt"]);
    await openFile(page, "hello.ts");
    await page.locator(".editor-review-toggle").click();
    const notes = sectionFor(page, "notes.txt");
    await selectFirstLine(page, notes);
    const firstLine = notes.locator(".view-line").first();
    await firstLine.click({ button: "right", position: { x: 20, y: 4 } });
    const menu = page.getByRole("menu");
    const revise = menu.getByRole("menuitem", { name: /^Revise Selection/ });
    await expect(revise.locator(".context-menu-keys")).not.toBeEmpty();
    await expect(menu.getByRole("menuitem", { name: /^Go to Definition/ })).toBeVisible();
    await revise.focus();
    await revise.press("Enter");
    await submitRevision(page);
    await expect
      .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
      .toBe("// revised by the fake\nkeep this second line\n");
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toBe(helloContent);

    await selectFirstLine(page, notes);
    await firstLine.click({ button: "right", position: { x: 20, y: 4 } });
    const cut = menu.getByRole("menuitem", { name: /^Cut/ });
    await cut.focus();
    await cut.press("Enter");
    await expect
      .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
      .toBe("\nkeep this second line\n");
    expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(
      "// revised by the fake",
    );
    await page.keyboard.press("ControlOrMeta+z");
    await expect
      .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
      .toBe("// revised by the fake\nkeep this second line\n");
    expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toBe(helloContent);
    await expect(page.locator(".editor-tab.active")).toContainText("hello.ts");
  });
});

test("deleted review snapshots expose Copy and disable mutating editor commands", async ({
  page,
  weavie,
}) => {
  const path = join(weavie.workspace, "notes.txt");
  await unlink(path);
  await runCommand(page, "Diff Against HEAD");
  await page.locator(".editor-empty-review").click();
  const notes = sectionFor(page, "notes.txt");
  await expect(notes.locator(".unified-review-file-name")).toHaveAttribute(
    "title",
    "Deleted file — review snapshot",
  );
  await notes
    .locator(".view-line")
    .first()
    .click({ button: "right", position: { x: 4, y: 4 } });
  const menu = page.getByRole("menu");
  for (const name of [/^Revise Selection/, /^Cut/, /^Paste/, /^Rename Symbol/]) {
    await expect(menu.getByRole("menuitem", { name })).toBeDisabled();
  }
  await expect(menu.getByRole("menuitem", { name: /^Copy/ })).toBeEnabled();
  await page.keyboard.press("Escape");
  await page.keyboard.press("ControlOrMeta+Alt+e");
  await expect(page.locator(".session-prompt-input")).toHaveCount(0);
  await expect(access(path)).rejects.toThrow();
});
