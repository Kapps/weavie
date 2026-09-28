import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { reviewScroll } from "../harness/review-scroll";

const paths = Array.from({ length: 5 }, (_, index) => `passive-${index}.txt`);
const content = (index: number, state: string): string =>
  Array.from({ length: 16 }, (_, line) => `${state} file ${index} line ${line}`).join("\n");

test.use({
  workspaceSeed: {
    run: async (workspace) => {
      await Promise.all(
        paths.map((path, index) => writeFile(join(workspace, path), content(index, "original"))),
      );
    },
  },
  fakeScript: {
    steps: paths.flatMap((path, index) => appliedEdit(path, content(index, "changed"))),
  },
});

test("first and return traversals paint every file without activating an editor", async ({
  page,
}) => {
  await expect(page.locator(".editor-empty-review")).toContainText("5");
  await page.locator(".editor-empty-review").click();
  const bodies = page.locator(".review-adaptive-body");
  await expect(bodies).toHaveCount(paths.length);
  const scrollbar = page.getByRole("scrollbar", { name: "Review scroll position", exact: true });
  const liveEditors = page.locator(".review-adaptive-live .monaco-editor");
  const traverse = async (direction: "PageDown" | "PageUp") => {
    const visited = new Set<number>();
    while (true) {
      const visible = await page.locator(".passive-review-body .view-line").evaluateAll((lines) => {
        const viewport = document.querySelector(".unified-review-diffs")!.getBoundingClientRect();
        return lines
          .filter((line) => {
            const rect = line.getBoundingClientRect();
            return rect.bottom > viewport.top && rect.top < viewport.bottom;
          })
          .map((line) => (line.textContent ?? "").replace(/\s+/g, " "));
      });
      for (const text of visible) {
        const match = text.match(/changed file (\d+) line/);
        if (match) visited.add(Number(match[1]));
      }
      await expect(liveEditors).toHaveCount(0);
      const previous = await reviewScroll(page);
      if (direction === "PageDown" ? previous.top >= previous.maximum : previous.top <= 0) break;
      await scrollbar.press(direction);
      await expect.poll(async () => (await reviewScroll(page)).top).not.toBe(previous.top);
      await page.evaluate(
        () =>
          new Promise<void>((resolve) =>
            requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
          ),
      );
    }
    expect([...visited].sort()).toEqual(paths.map((_, index) => index));
  };
  await expect(bodies.filter({ has: page.locator(".unified-review-notice") })).toHaveCount(0);
  await traverse("PageDown");
  const height = (await reviewScroll(page)).maximum;
  await traverse("PageUp");
  expect((await reviewScroll(page)).maximum).toBe(height);
  await expect(page.locator('.review-adaptive-body[data-presentation="passive"]')).toHaveCount(5);
});
