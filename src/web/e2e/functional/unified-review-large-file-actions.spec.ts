import { readdir, readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import { type HeadlessHost, restartAndReconnect } from "../harness/weavie-host";

// A GraphQL-codegen-shaped 300k-line file: its repeated lines ("};", "  id: ...") made the old exact alignment
// hang, so the review never appeared, review actions timed out, and restore stalled at "constructing".
const largeName = "generated.ts";
const baseline = Array.from({ length: 60_001 }, (_, index) => [
  `export type Type${index} = {`,
  `  __typename?: 'Type${index}';`,
  "  id: Scalars['ID']['output'];",
  "  name?: Maybe<Scalars['String']['output']>;",
  "};",
]).flat();
const edits = new Map([
  [11, "  id: Scalars['UUID']['output'];"],
  [150_002, "  name: Scalars['String']['output'];"],
  [299_996, "export type Renamed59999 = {"],
]);
const changed = baseline.map((line, index) => edits.get(index) ?? line);
const files = [largeName, "app.ts", "README.md"];

test.use({
  fakeScript: {
    steps: [
      { op: "edit", path: `{{WORKSPACE}}/${largeName}`, content: baseline.join("\n") },
      { op: "edit", path: "{{WORKSPACE}}/app.ts", content: "export const app = 1;\n" },
      { op: "edit", path: "{{WORKSPACE}}/README.md", content: "# Demo\n" },
      ...appliedEdit(largeName, changed.join("\n")),
      ...appliedEdit("app.ts", "export const app = 2;\n"),
      ...appliedEdit("README.md", "# Demo\n\nNow with docs.\n"),
    ],
  },
});

function largeSection(page: Page): Locator {
  return page.locator(".unified-review-file", {
    has: page.locator(".unified-review-file-name", { hasText: largeName }),
  });
}

async function openReview(page: Page): Promise<Locator> {
  await awaitReviewSet(page, files);
  await page.locator(".editor-empty-review").click();
  const section = largeSection(page);
  await expect(section.locator(".weavie-inline-pending-keep")).toHaveCount(3);
  return section;
}

const timeouts = (log: string): string[] =>
  log.split("\n").filter((line) => line.includes("[message] timed out"));

test("keep, revert, keep file and close complete on a 300k-line file", async ({ page, weavie }) => {
  test.slow();
  const section = await openReview(page);
  await section.locator(".weavie-inline-pending-keep").first().click();
  await expect(section.locator(".weavie-inline-accepted-kept")).toHaveCount(1);
  await expect(section.locator(".weavie-inline-pending-keep")).toHaveCount(2);

  const disk = join(weavie.workspace, largeName);
  await section.locator(".weavie-inline-pending-revert").first().click();
  await expect(section.locator(".weavie-inline-pending-keep")).toHaveCount(1);
  await expect
    .poll(async () => (await readFile(disk, "utf8")).split("\n")[150_002])
    .toBe(baseline[150_002]);

  await section.locator(".unified-review-file-action.keep").click();
  await expect(section.locator(".unified-review-file-action.keep")).toHaveCount(0);
  await page.locator(".editor-review-close").click();
  await expect(page.locator(".unified-review")).toHaveCount(0);
  const lines = (await readFile(disk, "utf8")).split("\n");
  expect([lines[11], lines[150_002], lines[299_996]]).toEqual([
    changed[11],
    baseline[150_002],
    changed[299_996],
  ]);
  expect(timeouts(weavie.log())).toEqual([]);
});

test("a pending review on a 300k-line file restores after restart", async ({ page, weavie }) => {
  test.slow();
  const section = await openReview(page);
  await section.locator(".weavie-inline-pending-keep").first().click();
  await expect(section.locator(".weavie-inline-pending-keep")).toHaveCount(2);

  await writeFakeScript(weavie.home, []);
  const logs = join(weavie.home, ".weavie", "logs");
  const before = new Set(await readdir(logs));
  const restartLog = async (): Promise<string> => {
    const fresh = (await readdir(logs)).filter((name) => !before.has(name));
    return (await Promise.all(fresh.map((name) => readFile(join(logs, name), "utf8")))).join("\n");
  };
  await restartAndReconnect(page, weavie as HeadlessHost);
  await expect.poll(restartLog).toContain("backend ready");
  expect(await restartLog()).toContain("sessions restored");
  // The review tab itself is restored, still showing the huge file's kept and pending hunks.
  await expect(largeSection(page).locator(".weavie-inline-pending-keep")).toHaveCount(2);
  await expect(largeSection(page).locator(".weavie-inline-accepted-kept")).toHaveCount(1);
  expect(timeouts(weavie.log() + (await restartLog()))).toEqual([]);
});
