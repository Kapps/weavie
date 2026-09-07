import type { Locator } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

const lineCount = 4_000;
const lines = (prefix: string): string =>
  Array.from({ length: lineCount }, (_, index) => `${prefix} line ${index}`).join("\n");

async function expectBoundedEditor(section: Locator, scroller: Locator): Promise<void> {
  const viewportHeight = await scroller.evaluate((element) => element.clientHeight);
  await expect
    .poll(() => section.locator(".monaco-editor").evaluate((element) => element.clientHeight))
    .toBeLessThanOrEqual(viewportHeight);
  await expect.poll(() => section.locator(".view-line").count()).toBeLessThan(100);
}

async function expectUnobscuredLine(section: Locator, line: Locator): Promise<void> {
  await expect(line).toBeInViewport();
  await expect
    .poll(async () => {
      const header = await section.locator(".unified-review-file-header").boundingBox();
      const bounds = await line.boundingBox();
      if (header === null || bounds === null) throw new Error("review line or header is missing");
      return bounds.y - header.y - header.height;
    })
    .toBeGreaterThanOrEqual(0);
}

test.describe("unified review mode — large addition", () => {
  test.use({
    fakeScript: {
      steps: appliedEdit(
        "large-review.txt",
        lines("new").replace("new line 0", `new line 0 ${"wide content ".repeat(100)}`),
      ),
    },
  });

  test("bounds rendering while keyboard navigation reaches both ends of a 4,000-line change", async ({
    page,
  }) => {
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file");
    const scroller = page.locator(".unified-review-diffs");
    await expect(section.locator(".monaco-editor")).toBeVisible();
    await expectBoundedEditor(section, scroller);
    await expect(section.locator(".view-line", { hasText: "new line 3999" })).toHaveCount(0);
    const firstLine = section.locator(".view-line", { hasText: /^new\sline\s0\s/ });
    await firstLine.click({ position: { x: 10, y: 10 } });

    await page.keyboard.press("ControlOrMeta+End");
    await expectUnobscuredLine(
      section,
      section.locator(".view-line", { hasText: "new line 3999" }),
    );
    await expectBoundedEditor(section, scroller);
    await page.keyboard.press("ControlOrMeta+Home");
    await expectUnobscuredLine(section, firstLine);
    await expectBoundedEditor(section, scroller);
    const left = await firstLine.evaluate((element) => element.getBoundingClientRect().left);
    await firstLine.hover({ position: { x: 10, y: 10 } });
    await page.mouse.wheel(800, 0);
    await expect
      .poll(() => firstLine.evaluate((element) => element.getBoundingClientRect().left))
      .toBeLessThan(left - 100);
  });
});

test.describe("unified review mode — large replacement", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "edit", path: "{{WORKSPACE}}/replacement.txt", content: lines("old") },
        ...appliedEdit("replacement.txt", lines("new")),
      ],
    },
  });

  test("bounds pending and accepted ghosts through wheel scrolling and reopening a kept file", async ({
    page,
  }) => {
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file");
    const scroller = page.locator(".unified-review-diffs");
    const ghost = section.locator(".weavie-inline-removed-content");
    const renderedGhostLines = (): Promise<number> =>
      ghost.evaluate((element) => (element.textContent ?? "").split("\n").length);
    await expect(section.locator(".monaco-editor")).toBeVisible();

    for (const reviewed of [false, true]) {
      await scroller.evaluate((element) => element.scrollTo(0, 0));
      await expect(ghost).toContainText("old line 0");
      await expect.poll(renderedGhostLines).toBeLessThan(100);
      await expectBoundedEditor(section, scroller);
      await expect(section.locator(".weavie-inline-removed-faded")).toHaveCount(reviewed ? 1 : 0);

      const bounds = await scroller.boundingBox();
      if (bounds === null) throw new Error("review viewport is missing");
      await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
      await page.mouse.wheel(0, 1_200);
      await expect
        .poll(() => scroller.evaluate((element) => element.scrollTop))
        .toBeGreaterThan(500);
      await expect(ghost).not.toContainText("old line 0");
      await expect.poll(renderedGhostLines).toBeLessThan(100);

      await scroller.evaluate((element) =>
        element.scrollTo(0, (element.scrollHeight - element.clientHeight) / 2),
      );
      await expect(ghost).toContainText("old line 3999");
      await expect.poll(renderedGhostLines).toBeLessThan(100);
      await scroller.evaluate((element) => element.scrollTo(0, element.scrollHeight));
      await expect(section.locator(".view-line", { hasText: "new line 3999" })).toBeInViewport();
      await expectBoundedEditor(section, scroller);
      if (reviewed) {
        await expect(section.locator(".weavie-inline-accepted").first()).toBeVisible();
      }
      await scroller.evaluate((element) => element.scrollTo(0, 0));
      await expect(ghost).toContainText("old line 0");
      await expect.poll(renderedGhostLines).toBeLessThan(100);

      if (!reviewed) {
        await section.locator(".unified-review-file-action.keep").click();
        await expect(section.locator(".unified-review-status")).toHaveText("Reviewed");
        await expect(section.locator(".monaco-editor")).toHaveCount(0);
        await section.locator(".unified-review-file-toggle").click();
        await expect(section.locator(".monaco-editor")).toBeVisible();
      }
    }
  });
});
