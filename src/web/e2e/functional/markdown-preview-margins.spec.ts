import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

test.use({ markdownView: "preview" });

const LONG = Array.from({ length: 60 }, (_, index) => `Paragraph ${index}.`).join("\n\n");

for (const banner of ["New file", "Mostly rewritten"]) {
  test.describe(banner, () => {
    test.use({
      workspaceSeed: {
        run: async (workspace: string) => {
          if (banner === "Mostly rewritten")
            await writeFile(join(workspace, "long.md"), "# Previous\n\nOld content.\n");
        },
      },
      fakeScript: { steps: appliedEdit("long.md", LONG) },
    });

    test("margin clicks keep the reading position and do not edit the document", async ({
      page,
      weavie,
    }) => {
      await expect(page.locator(".editor-empty-review")).toBeVisible();
      await openFile(page, "long.md");
      await expect(page.locator(".wv-md-banner")).toContainText(banner);
      const pane = page.locator(".editor-preview");
      const bounds = await pane.boundingBox();
      if (bounds === null) throw new Error("The markdown pane must be visible.");

      for (const scroll of [0, 500]) {
        await pane.evaluate((element, top) => element.scrollTo({ top }), scroll);
        await expect.poll(() => pane.evaluate((element) => element.scrollTop)).toBe(scroll);
        for (const x of [bounds.x + 8, bounds.x + bounds.width - 16]) {
          await page.mouse.click(x, bounds.y + bounds.height / 2);
          await expect(page.locator(".wv-preview-block-input")).toHaveCount(0);
          expect(await pane.evaluate((element) => element.scrollTop)).toBe(scroll);
        }
      }
      expect(await readFile(join(weavie.workspace, "long.md"), "utf8")).toBe(LONG);

      await pane.evaluate((element) => element.scrollTo({ top: element.scrollHeight }));
      const last = await page.locator(".editor-preview-body > .md-unit").last().boundingBox();
      if (last === null) throw new Error("The last paragraph must be visible.");
      await page.mouse.click(last.x + last.width / 2, last.y + last.height + 20);
      const input = page.locator(".wv-preview-block-input");
      await expect(input).toBeFocused();
      await expect(input).toHaveValue("");
      await input.fill("Appended paragraph");
      await page.keyboard.press("Escape");
      await expect
        .poll(() => readFile(join(weavie.workspace, "long.md"), "utf8"))
        .toContain(`${LONG}\n\nAppended paragraph`);
    });
  });
}

test("blank documents and the space below the text remain editable", async ({ page, weavie }) => {
  await writeFile(join(weavie.workspace, "blank.md"), "");
  await openFile(page, "blank.md");
  const body = page.locator(".editor-preview-body");
  const input = page.locator(".wv-preview-block-input");
  await body.click();
  await expect(input).toBeFocused();
  await input.fill("First paragraph");
  await page.keyboard.press("Escape");
  await expect(body).toContainText("First paragraph");

  const bounds = await body.boundingBox();
  if (bounds === null) throw new Error("The markdown body must be visible.");
  const y = bounds.y + bounds.height + 10;
  for (const x of [bounds.x - 10, bounds.x + bounds.width + 10]) {
    await page.mouse.click(x, y);
    await expect(input).toHaveCount(0);
  }
  await page.mouse.click(bounds.x + bounds.width / 2, y);
  await expect(input).toBeFocused();
  await input.fill("Second paragraph");
  await page.keyboard.press("Escape");
  await expect(body.locator("p")).toHaveText(["First paragraph", "Second paragraph"]);
  await expect
    .poll(() => readFile(join(weavie.workspace, "blank.md"), "utf8"))
    .toContain("First paragraph\n\nSecond paragraph");
});
