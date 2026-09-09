import type { Locator } from "@playwright/test";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

const baseline = Array.from({ length: 170 }, (_, index) => `// comment ${index}`);
const changed = baseline.map((line, index) => (index % 20 === 10 ? `${line} updated` : line));
const files = ["a-centered.ts", "b-centered.ts"];

test.use({
  fakeScript: {
    steps: files.flatMap((path) => [
      { op: "edit" as const, path: `{{WORKSPACE}}/${path}`, content: baseline.join("\n") },
      ...appliedEdit(path, changed.join("\n")),
    ]),
  },
});

async function expectCenteredLine(section: Locator, text: string): Promise<void> {
  const line = section.locator(".view-line", { hasText: text });
  await expect(line).toBeInViewport();
  await expect
    .poll(async () => {
      const scroller = await section.page().locator(".unified-review-diffs").boundingBox();
      const header = await section.locator(".unified-review-file-header").boundingBox();
      const bounds = await line.boundingBox();
      if (scroller === null || header === null || bounds === null) {
        throw new Error("Review viewport, header, or changed line is missing");
      }
      const center = (scroller.y + header.height + scroller.y + scroller.height) / 2;
      return Math.abs(bounds.y + bounds.height / 2 - center);
    })
    .toBeLessThanOrEqual(25);
}

for (const keepFile of ["toolbar", "file header"]) {
  test(`Next and Keep center changes, then ${keepFile} Keep opens the next file at the top`, async ({
    page,
  }) => {
    await awaitReviewSet(page, files);
    await page.locator(".editor-empty-review").click();
    await page.locator(".unified-review-tree-row.file", { hasText: files[0] }).click();
    const toolbar = page.locator(".weavie-inline-toolbar");
    const counter = toolbar.locator(".weavie-inline-stack-sub");
    const first = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: files[0] }),
    });
    await expect(counter).toContainText("change 1/8");
    await toolbar.locator("button[title^='Next change']").click();
    await expect(counter).toContainText("change 2/8");
    await expectCenteredLine(first, "// comment 30 updated");
    await toolbar.locator(".weavie-inline-accept").click();
    await expect(counter).toContainText("change 2/7");
    await expectCenteredLine(first, "// comment 50 updated");

    if (keepFile === "toolbar") {
      await toolbar.locator(".weavie-inline-scope-btn").click();
      await toolbar.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
      await toolbar.locator(".weavie-inline-accept").click();
    } else {
      await first.locator(".unified-review-file-action.keep").click();
    }
    await expect(first.locator(".unified-review-status")).toHaveText("Reviewed");
    await expect(toolbar.locator(".weavie-inline-stack-name")).toHaveText(files[1]);
    const second = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: files[1] }),
    });
    await expect
      .poll(async () => {
        const header = await second.locator(".unified-review-file-header").boundingBox();
        const viewport = await page.locator(".unified-review-diffs").boundingBox();
        if (header === null || viewport === null) throw new Error("Next file header is missing");
        const offset = header.y - viewport.y;
        return offset >= 0 && offset <= 25;
      })
      .toBe(true);
    await expect(second.locator(".view-line", { hasText: /^\/\/\scomment\s7$/ })).toBeInViewport();
  });
}
