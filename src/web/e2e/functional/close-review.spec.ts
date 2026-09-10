import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { normalizePath } from "../../src/editor/fs-path";
import { openFile, runCommand } from "../harness/actions";
import { writeFakeScript } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { appliedEdit } from "../harness/review";
import type { HeadlessHost } from "../harness/weavie-host";

const ORIGINAL = "just plain text\n";
const CHANGED = `${ORIGINAL}keep this addition\n`;

async function expectClosed(page: Page): Promise<void> {
  await expect(page.locator(".editor-review-close")).toHaveCount(0);
  await expect(page.locator(".editor-review-open")).toHaveCount(0);
  await expect(page.locator(".unified-review")).toHaveCount(0);
  await expect(page.locator(".editor-tab", { hasText: "Review Changes" })).toHaveCount(0);
  await expect(page.locator(".weavie-inline-toolbar")).toHaveCount(0);
  await expect(page.locator(".weavie-inline-added, .weavie-inline-accepted")).toHaveCount(0);
}

test.describe("close diff", () => {
  test.use({ fakeScript: { steps: appliedEdit("notes.txt", CHANGED) } });

  for (const action of ["Close Diff", "Keep Change"]) {
    test(`${action} closes unified review and stays closed after unload and restart`, async ({
      page,
      weavie,
    }) => {
      test.slow();
      await openFile(page, "notes.txt");
      await expect(page.locator(".weavie-inline-added")).toBeVisible();
      await page.locator(".editor-review-open").click();
      await expect(page.locator(".unified-review .weavie-inline-added")).toBeVisible();
      const close = page.locator(".editor-review-close");
      await expect(close).toHaveAttribute("title", /Accept remaining changes and close diff.*\(/);
      if (action === "Close Diff") {
        await close.click();
      } else {
        await page.locator(".unified-review .weavie-inline-pending-keep").click();
      }
      await expectClosed(page);
      expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);

      await writeFakeScript(weavie.home, []);
      await runCommand(page, "Unload Session");
      await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
      await page.locator(".session-chip.unloaded").click();
      await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
      await openFile(page, "notes.txt");
      await expectClosed(page);

      await page.goto("about:blank");
      await (weavie as HeadlessHost).restart();
      const connect = await page.request.post(weavie.url, {
        form: { token: weavie.token },
        maxRedirects: 0,
      });
      expect(connect.status()).toBe(302);
      await page.goto(weavie.url);
      await expect(page.locator("#splash")).toHaveCount(0);
      await openFile(page, "notes.txt");
      await expectClosed(page);
      expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
    });
  }

  for (const decision of ["Keep All Changes", "Undo All Changes"]) {
    test(`the fully reviewed state closes after ${decision}`, async ({ page, weavie }) => {
      await openFile(page, "notes.txt");
      await expect(page.locator(".weavie-inline-added")).toBeVisible();
      await page.locator(".editor-review-open").click();
      await expect(page.locator(".unified-review .weavie-inline-added")).toBeVisible();
      await runCommand(page, decision);
      if (decision === "Undo All Changes") {
        await page.getByRole("button", { name: "Revert all", exact: true }).click();
      }
      await expect(page.locator(".weavie-inline-added")).toHaveCount(0);
      if (decision === "Undo All Changes") {
        await expect(page.locator(".unified-review")).toBeVisible();
        await expect(page.locator(".weavie-inline-hist").first()).toBeEnabled();
        await runCommand(page, "Undo Revert (Review)");
        await expect(page.locator(".unified-review .weavie-inline-added")).toBeVisible();
        await page.locator(".unified-review .weavie-inline-pending-revert").click();
        await expect
          .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
          .toBe(ORIGINAL);
        await expect(page.locator(".unified-review")).toBeVisible();
        await page.locator(".editor-review-close").click();
      }
      await expectClosed(page);
      expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(
        decision === "Keep All Changes" ? CHANGED : ORIGINAL,
      );
    });
  }
});

test.describe("last file acceptance", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("notes.txt", CHANGED),
        ...appliedEdit("README.md", "# A changed readme\n"),
      ],
    },
  });

  test("keeping a file stays undoable while another is pending and the last file closes", async ({
    page,
  }) => {
    await openFile(page, "notes.txt");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await page.locator(".editor-review-open").click();
    const notes = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: "notes.txt" }),
    });
    const readme = page.locator(".unified-review-file", {
      has: page.locator(".unified-review-file-name", { hasText: "README.md" }),
    });
    await expect(readme.locator(".unified-review-file-action.keep")).toBeVisible();
    await notes.locator(".unified-review-file-action.keep").click();
    await expect(notes.locator(".unified-review-status")).toHaveText("Reviewed");
    await expect(page.locator(".weavie-inline-hist").first()).toBeEnabled();
    await readme.locator(".unified-review-file-action.keep").click();
    await expectClosed(page);
  });
});

test("file review closes from the keyboard and the same comparison opens fresh", async ({
  page,
  weavie,
}) => {
  await writeFile(join(weavie.workspace, "notes.txt"), CHANGED);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".weavie-inline-added")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Alt+w");
  await expectClosed(page);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".weavie-inline-added")).toBeVisible();
  await expect(page.locator(".weavie-inline-accepted")).toHaveCount(0);
  await runCommand(page, "Close Diff");
  await expectClosed(page);
  expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
});

test("a comparison with no remaining changed files still offers Close Diff", async ({
  page,
  weavie,
}) => {
  await writeFile(join(weavie.workspace, "notes.txt"), CHANGED);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".weavie-inline-added")).toBeVisible();
  await writeFile(join(weavie.workspace, "notes.txt"), ORIGINAL);
  await runCommand(page, "Diff Against HEAD");
  await expect(page.locator(".toast", { hasText: "No changes against 'HEAD'" })).toBeVisible();
  await expect(page.locator(".editor-review-open")).toHaveCount(0);
  await expect(page.locator(".editor-review-close")).toBeVisible();
  await runCommand(page, "Close Diff");
  await expectClosed(page);
});

test.describe("agent edits after closing", () => {
  const next = `${CHANGED}a later agent addition\n`;
  const signal = ".weavie-e2e-next-edit";
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("notes.txt", CHANGED),
        { op: "waitFile", path: `{{WORKSPACE}}/${signal}` },
        { op: "hook", request: { hook_event_name: "UserPromptSubmit" } },
        ...appliedEdit("notes.txt", next),
      ],
    },
  });

  test("new edits review only the changes made after closing", async ({ page, weavie }) => {
    await openFile(page, "notes.txt");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await page.locator(".editor-review-close").click();
    await expectClosed(page);
    await writeFile(join(weavie.workspace, signal), "");
    await expect(page.locator(".weavie-inline-added")).toBeVisible();
    await runCommand(page, "Undo All Changes");
    await page.getByRole("button", { name: "Revert all", exact: true }).click();
    await expect.poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(CHANGED);
  });
});

test.describe("three-file completion", () => {
  const changes = [
    ["a-review.ts", 'export const first = "accepted";\n'],
    ["b-review.ts", 'export const second = "accepted";\n'],
    ["c-review.ts", 'export const third = "accepted";\n'],
  ] as const;
  test.use({
    fakeScript: { steps: changes.flatMap(([path, content]) => appliedEdit(path, content)) },
  });

  test("accepting three files in turn closes Review Changes and restores the underlying file", async ({
    page,
    weavie,
  }) => {
    await awaitReviewSet(
      page,
      changes.map(([path]) => path),
    );
    const original = await readFile(join(weavie.workspace, "notes.txt"), "utf8");
    await openFile(page, "notes.txt");
    await page.locator(".editor-review-open").click();
    const reviewTab = page.locator(".editor-tab", { hasText: "Review Changes" });
    const overview = page.locator(".unified-review");
    await expect(reviewTab).toHaveClass(/\bactive\b/);
    await expect(overview.locator(".unified-review-files-header")).toContainText("3 changed files");
    await overview.locator(".unified-review-tree-row.file", { hasText: changes[0][0] }).click();
    const toolbar = overview.locator(".weavie-inline-toolbar");

    for (const [index, [path]] of changes.entries()) {
      await expect(toolbar.locator(".weavie-inline-stack-name")).toHaveText(path);
      await expect(toolbar.locator(".weavie-inline-stack-sub")).toContainText("change 1/1");
      await toolbar.locator(".weavie-inline-accept").click();
      if (index < changes.length - 1) {
        await expect(reviewTab).toHaveClass(/\bactive\b/);
        await expect(
          overview
            .locator(".unified-review-file", { hasText: path })
            .locator(".unified-review-status"),
        ).toHaveText("Reviewed");
      }
    }

    await expectClosed(page);
    await expect(page.locator(".editor-tab.active", { hasText: "notes.txt" })).toBeVisible();
    // Flaked on Windows CI 2026-09-09 16:07 UTC (run 34374758357): data-active-file is lowercase-drive/
    // forward-slash normalized, but this compared it against an OS-native path.join with an uppercase
    // drive and backslashes. Normalize both sides, matching recent-files.spec.ts.
    await expect
      .poll(async () =>
        normalizePath((await page.locator(".editor").getAttribute("data-active-file")) ?? ""),
      )
      .toBe(normalizePath(join(weavie.workspace, "notes.txt")));
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getValue()))
      .toBe(original);
    for (const [path, content] of changes) {
      expect(await readFile(join(weavie.workspace, path), "utf8")).toBe(content);
    }
    expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toBe(original);
    await expect(
      page.locator(".toast", { hasText: "This file is no longer in the review" }),
    ).toHaveCount(0);
  });
});
