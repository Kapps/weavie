import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
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

async function expectUnobscuredLine(line: Locator): Promise<void> {
  await expect(line).toBeInViewport();
  await expect
    .poll(() =>
      line.evaluate((element) => {
        const header = element
          .closest(".unified-review-file")
          ?.querySelector(".unified-review-file-header")
          ?.getBoundingClientRect();
        const toolbar = document.querySelector(".weavie-inline-toolbar")?.getBoundingClientRect();
        if (header === undefined || toolbar === undefined) {
          throw new Error("review header or toolbar is missing");
        }
        const bounds = element.getBoundingClientRect();
        return Math.min(bounds.y - header.bottom, toolbar.y - bounds.bottom);
      }),
    )
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
    weavie,
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
    await expectUnobscuredLine(section.locator(".view-line", { hasText: "new line 3999" }));
    await expectBoundedEditor(section, scroller);
    await scroller.evaluate((element) => element.scrollTo(0, element.scrollHeight));
    const bottomBeforeTyping = await scroller.evaluate((element) => element.scrollTop);
    const revisionBeforeTyping = await page.evaluate(() => window.__WEAVIE_REVIEW__?.rev);
    await page.keyboard.type(" edited at the end");
    await expect
      .poll(() => readFile(join(weavie.workspace, "large-review.txt"), "utf8"))
      .toContain("new line 3999 edited at the end");
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_REVIEW__?.rev))
      .not.toBe(revisionBeforeTyping);
    await expect
      .poll(() =>
        scroller.evaluate(
          (element) => element.scrollHeight - element.clientHeight - element.scrollTop,
        ),
      )
      .toBeLessThanOrEqual(1);
    await expect
      .poll(async () =>
        Math.abs((await scroller.evaluate((element) => element.scrollTop)) - bottomBeforeTyping),
      )
      .toBeLessThanOrEqual(1);
    await expectUnobscuredLine(
      section.locator(".view-line", { hasText: "new line 3999 edited at the end" }),
    );
    await page.keyboard.press("ControlOrMeta+Home");
    await expectUnobscuredLine(firstLine);
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
        ...appliedEdit("z-pending.txt", "a pending change\n"),
      ],
    },
  });

  test("bounds pending and accepted ghosts through wheel scrolling and reopening a kept file", async ({
    page,
  }) => {
    await awaitReviewSet(page, ["replacement.txt", "z-pending.txt"]);
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: "replacement.txt" }),
    });
    const scroller = page.locator(".unified-review-diffs");
    const ghost = section.locator(".weavie-inline-removed-content");
    const toolbar = page.locator(".weavie-inline-toolbar");
    const renderedGhostLines = (): Promise<number> =>
      ghost.evaluate((element) => (element.textContent ?? "").split("\n").length);
    await expect(section.locator(".monaco-editor")).toBeVisible();

    for (const reviewed of [false, true]) {
      await expect(toolbar).toHaveCount(1);
      await expect(toolbar).toBeVisible();
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
      await expectUnobscuredLine(section.locator(".view-line", { hasText: "new line 3999" }));
      await expectBoundedEditor(section, scroller);
      if (reviewed) {
        await expect(section.locator(".weavie-inline-accepted").first()).toBeVisible();
      }
      await scroller.evaluate((element) => element.scrollTo(0, 0));
      await expect(ghost).toContainText("old line 0");
      await expect.poll(renderedGhostLines).toBeLessThan(100);

      if (!reviewed) {
        await toolbar.locator(".weavie-inline-accept").click();
        await expect(section.locator(".unified-review-status")).toHaveText("Reviewed");
        await expect(section.locator(".monaco-editor")).toHaveCount(0);
        await section.locator(".unified-review-file-toggle").click();
        await expect(section.locator(".monaco-editor")).toBeVisible();
      }
    }
    await toolbar.locator(".weavie-inline-hist").first().click();
    await expect(toolbar.locator(".weavie-inline-stack-sub")).toContainText("change 1/1");
    await scroller.evaluate((element) => element.scrollTo(0, 0));
    await expect(ghost).toContainText("old line 0");
    await expect(section.locator(".weavie-inline-removed-faded")).toHaveCount(0);
    await expect.poll(renderedGhostLines).toBeLessThan(100);
    await expectBoundedEditor(section, scroller);
  });
});

test.describe("unified review mode — large separated changes", () => {
  const baseline = Array.from({ length: lineCount }, (_, index) => `old line ${index}`);
  const content = baseline
    .map((line, index) => (index < 1_000 || index >= 3_000 ? `new line ${index}` : line))
    .join("\n");
  test.use({
    fakeScript: {
      steps: [
        { op: "edit", path: "{{WORKSPACE}}/separated.txt", content: baseline.join("\n") },
        ...appliedEdit("separated.txt", content),
      ],
    },
  });

  test("the normal toolbar navigates, keeps, undoes, and reverts the visible large change", async ({
    page,
    weavie,
  }) => {
    await page.locator(".editor-empty-review").click();
    const section = page.locator(".unified-review-file");
    const scroller = page.locator(".unified-review-diffs");
    const toolbar = page.locator(".weavie-inline-toolbar");
    const counter = toolbar.locator(".weavie-inline-stack-sub");
    await expect(toolbar).toHaveCount(1);
    await expect(counter).toContainText("change 1/2");
    await toolbar.locator("button[title^='Next change']").click();
    await expect(counter).toContainText("change 2/2");
    await expectBoundedEditor(section, scroller);

    await scroller.evaluate((element) => element.scrollTo(0, 0));
    await expect(counter).toContainText("change 1/2");
    await scroller.hover();
    await page.mouse.wheel(0, 200_000);
    await expect(counter).toContainText("change 2/2");
    await expectUnobscuredLine(section.locator(".view-line", { hasText: "new line 3999" }));
    await toolbar.locator(".weavie-inline-accept").click();
    await expect(counter).toContainText("change 1/1");
    await expect
      .poll(() => readFile(join(weavie.workspace, "separated.txt"), "utf8"))
      .toBe(content);
    await toolbar.locator(".weavie-inline-hist").first().click();
    await expect(counter).toContainText("change 2/2");

    await scroller.evaluate((element) => element.scrollTo(0, element.scrollHeight));
    await expect(counter).toContainText("change 2/2");
    await toolbar.locator(".weavie-inline-reject").click();
    await expect
      .poll(() => readFile(join(weavie.workspace, "separated.txt"), "utf8"))
      .toBe(baseline.map((line, index) => (index < 1_000 ? `new line ${index}` : line)).join("\n"));
    await expect(counter).toContainText("change 1/1");
    await expect(toolbar).toHaveCount(1);
    await expectBoundedEditor(section, scroller);
  });
});
