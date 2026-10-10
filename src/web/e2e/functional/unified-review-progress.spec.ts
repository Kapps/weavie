import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit, reviewFileSegment } from "../harness/review";

// Two one-line hunks, far enough apart to stay separate: each is one removed + one added line.
const baseline = Array.from({ length: 30 }, (_, index) => `line ${index}`);
const edited = (lines: number[]): string =>
  baseline.map((line, index) => (lines.includes(index) ? `${line} changed` : line)).join("\n");

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: "{{WORKSPACE}}/progress.txt", content: baseline.join("\n") },
      ...appliedEdit("progress.txt", edited([2, 20])),
      ...appliedEdit("other.txt", "another file\n"),
      { op: "waitFile", path: "{{WORKSPACE}}/.second-edit" },
      ...appliedEdit("progress.txt", edited([2, 11, 20])),
    ],
  },
});

test("the review strip fills as changes are kept and drops when the agent edits again", async ({
  page,
  weavie,
}) => {
  await awaitReviewSet(page, ["progress.txt", "other.txt"]);
  await page.locator(".editor-empty-review").click();
  const overview = page.locator(".unified-review");
  const segment = reviewFileSegment(overview, "progress.txt");
  await expect(segment).toHaveAttribute("title", "progress.txt +2 −2 · 0% reviewed");

  await segment.click();
  await expect(segment).toHaveAttribute("aria-current", "true");
  const toolbar = overview.locator(".weavie-inline-toolbar");
  await toolbar.locator(".weavie-inline-accept").click();
  await expect(segment).toHaveAttribute("title", /· 50% reviewed$/);
  await expect
    .poll(() =>
      segment.evaluate((element) => getComputedStyle(element).getPropertyValue("--reviewed")),
    )
    .toBe("50%");
  expect(await segment.evaluate((element) => getComputedStyle(element).flexGrow)).toBe("4");

  await toolbar.locator(".weavie-inline-accept").click();
  await expect(segment).toHaveAttribute("title", /· 100% reviewed$/);

  await writeFile(join(weavie.workspace, ".second-edit"), "go\n");
  await expect(segment).toHaveAttribute("title", "progress.txt +3 −3 · 67% reviewed");
});
