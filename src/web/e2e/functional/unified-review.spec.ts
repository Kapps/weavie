import { mkdir, readFile, unlink, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import type { WeavieWindow } from "../harness/weavie-window";

const HELLO =
  "export function greet(name: string): string {\n" +
  "  return `Hello from unified review, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";

// Every changed file in the unified overview is a real Monaco editor on the file's live working copy, so it
// carries the same tokenization, LSP and editing the file-review pane does. `.mtk<n>` spans are the proof
// tokens were produced rather than plain text — same signal editor.spec.ts uses.
const sectionFor = (page: Page, name: string): Locator =>
  page.locator(".unified-review-file", { has: page.locator(`text=${name}`) });

// The pixel gap between consecutive sections. The virtualizer lays them out at a fixed 20px; anything larger is
// dead space from a row sitting on its pre-mount estimate instead of its measured height.
const sectionGaps = (page: Page): Promise<number[]> =>
  page.locator(".unified-review-file").evaluateAll((sections) => {
    const rects = sections.map((el) => el.getBoundingClientRect()).sort((a, b) => a.top - b.top);
    return rects.slice(1).map((rect, index) => Math.round(rect.top - rects[index].bottom));
  });

const distinctTokenClasses = (section: Locator): Promise<number> =>
  section
    .locator(".view-line [class*='mtk']")
    .evaluateAll(
      (spans) =>
        new Set(
          spans
            .flatMap((span) => span.className.split(/\s+/))
            .filter((name) => /^mtk\d+$/.test(name)),
        ).size,
    );

test.describe("unified review mode", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", HELLO),
        ...appliedEdit("notes.txt", "just plain text\na unified addition\n"),
      ],
    },
  });

  test("renders each file in a highlighted editor and toggles into the exact file review", async ({
    page,
  }) => {
    const cue = page.locator(".editor-empty-review");
    await expect(cue).toBeVisible({ timeout: 15_000 });
    await cue.click();

    const overview = page.locator(".unified-review");
    await expect(overview).toBeVisible();
    await expect(overview.locator(".unified-review-files-header")).toContainText("2 changed files");
    await expect(overview.locator(".unified-review-tree-row.directory")).toHaveCount(0);
    await expect(overview.locator(".unified-review-tree-row.file")).toHaveCount(2);
    await expect(
      overview.locator(".unified-review-tree-row.file", { hasText: "notes.txt" }),
    ).toContainText(/\+\d+.*−\d+/);
    await expect(overview.locator(".unified-review-file")).toHaveCount(2);

    // The change is marked up in the editor itself (added band + removed ghost), not as hand-rolled rows.
    const hello = sectionFor(page, "hello.ts");
    // See the 2026-09-03 flake note on the "completions" test below — same hardcoded-override defect.
    await expect(hello.locator(".monaco-editor")).toBeVisible();
    await expect(hello.locator(".weavie-inline-added").first()).toBeVisible();
    await expect(overview.locator(".unified-review-notice", { hasText: "Loading" })).toHaveCount(0);

    // …and it is tokenized: several distinct token classes, not one flat default run.
    await expect.poll(() => distinctTokenClasses(hello), { timeout: 15_000 }).toBeGreaterThan(2);

    const notes = sectionFor(page, "notes.txt");
    const notesDisclosure = notes.locator(".unified-review-file-toggle");
    await expect(notesDisclosure).toHaveAttribute("title", /Collapse notes\.txt.*Alt\+\[/);
    await notesDisclosure.focus();
    await page.keyboard.press("Alt+[");
    await expect(notesDisclosure).toHaveAttribute("aria-expanded", "false");
    await expect(hello.locator(".monaco-editor")).toBeVisible();
    await notesDisclosure.click();

    const disclosure = hello.locator(".unified-review-file-toggle");
    await disclosure.click();
    await expect(disclosure).toHaveAttribute("aria-expanded", "false");
    await expect(hello.locator(".monaco-editor")).toHaveCount(0);
    await disclosure.click();
    await expect(hello.locator(".monaco-editor")).toBeVisible();

    await hello.locator(".unified-review-file-name").click();
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toBeVisible();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });

    const mode = page.locator(".editor-review-toggle");
    await expect(mode).toContainText("Unified review");
    await expect(mode).toHaveAttribute("title", /Switch to unified review.*\(/);
    await mode.click();
    await expect(overview).toBeVisible();
    await expect(mode).toContainText("File review");

    await openFile(page, "README.md");
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab.active", { hasText: "README.md" })).toBeVisible();
  });

  test("a file-level keep leaves the change in the faded reviewed band", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    const notes = sectionFor(page, "notes.txt");
    await expect(notes.locator(".unified-review-file-action.keep")).toBeVisible({
      timeout: 15_000,
    });

    await notes.locator(".unified-review-file-action.keep").click();

    await expect(notes.locator(".unified-review-status")).toHaveText("Reviewed", {
      timeout: 15_000,
    });
    await expect(notes.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "false",
    );
    await expect(notes.locator(".monaco-editor")).toHaveCount(0);
    await expect(notes.locator(".unified-review-file-action.keep")).toHaveCount(0);

    await overview.locator(".unified-review-diffs").evaluate((element) => element.scrollTo(0, 0));
    await overview.locator(".unified-review-tree-row.file", { hasText: "notes.txt" }).click();
    await expect(notes.locator(".unified-review-file-toggle")).toHaveAttribute(
      "aria-expanded",
      "true",
    );
    await expect(notes.locator(".weavie-inline-accepted").first()).toBeVisible();

    // The push that lands the keep must not throw away the measured section heights: doing so re-spaces every
    // row below on its estimate and opens dead space that never heals.
    await expect.poll(() => sectionGaps(page)).toEqual([20]);
  });

  // Completions are the feature the hand-rolled rows could never have: they need a real editor on the real
  // model. The provider is mocked through __WEAVIE_MONACO__ (the harness has no language server), exactly as
  // editor-code-intel.spec.ts mocks definitions.
  test("completions open inside a review section editor", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const hello = sectionFor(page, "hello.ts");
    // Flaked on windows-latest 2026-09-03 01:54 UTC (run 33704819901, job 100492392393,
    // https://github.com/Kapps/weavie/actions/runs/33704819901/job/100492392393): the Monaco mount never
    // became visible inside 15s under runner contention, unrelated to the PR that surfaced it (a Mac
    // crash-reporting change). The 15s here was a hardcoded override that undercut playwright.config.ts's
    // own platform-aware `expect.timeout` (30s on Windows/macOS, raised there for exactly this kind of
    // full-stack mount latency) — every `.monaco-editor` wait in this file had the same override, so all
    // four are dropped to let them inherit that budget instead of capping it back down to the Linux value.
    await expect(hello.locator(".monaco-editor")).toBeVisible();
    await expect(hello.locator(".weavie-inline-added").first()).toBeVisible();

    await page.evaluate(() => {
      const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
      if (monaco === undefined) {
        throw new Error("monaco handle not available");
      }
      monaco.languages.registerCompletionItemProvider("*", {
        triggerCharacters: ["."],
        provideCompletionItems: (_model, position) => ({
          suggestions: [
            {
              label: "unifiedReviewCompletion",
              kind: 1,
              insertText: "unifiedReviewCompletion",
              range: {
                startLineNumber: position.lineNumber,
                startColumn: position.column,
                endLineNumber: position.lineNumber,
                endColumn: position.column,
              },
            },
          ],
        }),
      });
    });

    await hello.locator(".view-line", { hasText: "console.warn" }).click();
    await page.keyboard.press("End");
    await page.keyboard.type(".");

    await expect(page.locator(".suggest-widget")).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".suggest-widget")).toContainText("unifiedReviewCompletion");
  });

  // The sections are live working copies, so an edit made while reviewing lands on disk through the same
  // debounced save the file pane uses — the riskiest seam in wiring a second editor onto a tab's model. The
  // newline matters: it grows the section, which re-measures the virtualizer. Rows keyed by the virtualizer's
  // own item objects would rebuild every section's editor there, and the rest of the typing would land nowhere.
  test("editing a file in the overview keeps focus across a re-measure and saves it @cross", async ({
    page,
    weavie,
  }) => {
    test.slow();
    await page.locator(".editor-empty-review").click();
    const notes = sectionFor(page, "notes.txt");
    // See the 2026-09-03 flake note on the "completions" test above — same hardcoded-override defect.
    await expect(notes.locator(".monaco-editor")).toBeVisible();

    const marker = `edited-in-review-${Date.now()}`;
    await notes.locator(".view-line", { hasText: "a unified addition" }).click();
    await page.keyboard.press("End");
    await page.keyboard.type(`${marker}-one`);
    await page.keyboard.press("Enter");
    await page.keyboard.type(`${marker}-two`);

    const notesPath = join(weavie.workspace, "notes.txt");
    await expect
      .poll(() => readFile(notesPath, "utf8").catch(() => ""), { timeout: 15_000 })
      .toContain(`${marker}-one\n${marker}-two`);
  });
});

test.describe("unified review mode — file tree", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "waitFile", path: "{{WORKSPACE}}/.nested-ready" },
        ...appliedEdit("src/hello.ts", HELLO),
        ...appliedEdit("docs/notes.txt", "nested note\n"),
      ],
    },
  });

  test("groups nested files with diff sizes and collapsible folders", async ({ page, weavie }) => {
    await mkdir(join(weavie.workspace, "src"));
    await mkdir(join(weavie.workspace, "docs"));
    await writeFile(join(weavie.workspace, ".nested-ready"), "ready\n");
    await page.locator(".editor-empty-review").click();

    const overview = page.locator(".unified-review");
    await expect(overview.locator(".unified-review-tree-row.directory")).toHaveCount(2);
    await expect(overview.locator(".unified-review-tree-row.file")).toHaveCount(2);
    await expect(
      overview.locator(".unified-review-tree-row.file", { hasText: "notes.txt" }),
    ).toContainText(/\+\d+.*−\d+/);

    const docsFolder = overview.locator(".unified-review-tree-row.directory", {
      hasText: "docs",
    });
    const docsFile = overview.locator(".unified-review-tree-row.file", { hasText: "notes.txt" });
    const srcFolder = overview.locator(".unified-review-tree-row.directory", { hasText: "src" });
    const srcFile = overview.locator(".unified-review-tree-row.file", { hasText: "hello.ts" });
    await docsFolder.focus();
    await page.keyboard.press("ArrowRight");
    await expect(docsFile).toBeFocused();
    await page.keyboard.press("ArrowLeft");
    await expect(docsFolder).toBeFocused();
    await page.keyboard.press("ArrowLeft");
    await expect(docsFolder).toHaveAttribute("aria-expanded", "false");
    await expect(docsFile).toHaveCount(0);
    await page.keyboard.press("ArrowDown");
    await expect(srcFolder).toBeFocused();
    await page.keyboard.press("ArrowRight");
    await expect(srcFile).toBeFocused();
    await page.keyboard.press("Home");
    await expect(docsFolder).toBeFocused();
    await page.keyboard.press("ArrowRight");
    await expect(docsFolder).toHaveAttribute("aria-expanded", "true");
    await page.keyboard.press("End");
    await expect(srcFile).toBeFocused();
  });
});

test.describe("unified review mode — collapsed context", () => {
  const untouched = Array.from({ length: 200 }, (_, index) => `line ${index}`).join("\n");
  const changed = untouched.replace("line 100", "line 100 — changed by the agent");
  test.use({
    fakeScript: {
      steps: [
        // Seed the file outside the change tracker so it becomes the review baseline, then change one line.
        { op: "edit", path: "{{WORKSPACE}}/context.txt", content: `${untouched}\n` },
        ...appliedEdit("context.txt", `${changed}\n`),
      ],
    },
  });

  test("shows only the changed lines and their context, not the whole file", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const section = sectionFor(page, "context.txt");
    await expect(section.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 15_000 });

    // 1 changed line + 3 lines of context either side; the other ~193 lines are collapsed away.
    await expect.poll(() => section.locator(".view-line").count()).toBeLessThan(15);
    await expect(section.locator(".view-line", { hasText: "line 100 — changed" })).toHaveCount(1);
    await expect(section.locator(".view-line", { hasText: "line 5" })).toHaveCount(0);
  });
});

test("a cold deleted file renders from its review snapshot instead of reading the missing path", async ({
  page,
  weavie,
}) => {
  await unlink(join(weavie.workspace, "notes.txt"));
  await runCommand(page, "Diff Against HEAD");

  const cue = page.locator(".editor-empty-review");
  await expect(cue).toBeVisible({ timeout: 30_000 });
  await cue.click();

  const notes = sectionFor(page, "notes.txt");
  await expect(notes.locator(".monaco-editor")).toBeVisible();
  await expect(notes.locator(".weavie-inline-removed").first()).toBeVisible();
  await expect(notes.locator(".unified-review-notice", { hasText: "Couldn't open" })).toHaveCount(
    0,
  );
  await expect(notes.locator(".unified-review-file-name")).toHaveAttribute(
    "title",
    "Deleted file — review snapshot",
  );
  const mode = page.locator(".editor-review-toggle");
  await expect(mode).toBeDisabled();
  await expect(mode).toHaveAttribute("title", /File review unavailable.*\(/);
});

test.describe("unified review mode — large file", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit(
          "large-review.txt",
          Array.from({ length: 4_000 }, (_, index) => `line ${index}`).join("\n"),
        ),
      ],
    },
  });

  test("renders a 4,000-line change without a presentation cutoff", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    // See the 2026-09-03 flake note on the "completions" test above — same hardcoded-override defect.
    await expect(overview.locator(".monaco-editor")).toBeVisible();
    await expect(overview).not.toContainText("Diff calculation timed out");
    await expect(overview.locator(".view-line", { hasText: "line 3999" })).toHaveCount(1);
  });
});

test.describe("unified review mode — large file set", () => {
  const fileCount = 100;
  const readyFile = ".large-review-ready";
  test.use({
    fakeScript: {
      steps: [
        ...Array.from({ length: fileCount }, (_, index) =>
          appliedEdit(`review-${String(index).padStart(3, "0")}.txt`, `change ${index}\n`),
        ).flat(),
        { op: "edit", path: `{{WORKSPACE}}/${readyFile}`, content: "ready\n" },
      ],
    },
  });

  test("restores the exact file across a reverse mode toggle without mounting every editor", async ({
    page,
    weavie,
  }) => {
    test.slow();
    await expect
      .poll(() => readFile(join(weavie.workspace, readyFile), "utf8").catch(() => ""), {
        timeout: 60_000,
      })
      .toBe("ready\n");
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    const targetName = "review-099.txt";
    const targetLink = overview.locator(".unified-review-tree-row.file", { hasText: targetName });
    await expect(overview.locator(".unified-review-tree-row.file")).toHaveCount(fileCount);

    await targetLink.click();
    const targetSection = sectionFor(page, targetName);
    await expect(targetSection).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => overview.locator(".unified-review-file").count()).toBeLessThan(20);
    // Only the mounted sections hold an editor — 100 files never means 100 live Monaco instances.
    await expect
      .poll(() =>
        page.evaluate(() => (window as WeavieWindow).__WEAVIE_MONACO__?.editor.getEditors().length),
      )
      .toBeLessThan(20);

    await targetSection.locator(".unified-review-file-name").click();
    await expect(page.locator(".editor-tab.active", { hasText: targetName })).toBeVisible();

    await page.locator(".editor-review-toggle").click();
    await expect(overview).toBeVisible();
    await expect(targetSection).toBeVisible();

    await page.locator(".editor-review-toggle").click();
    await expect(page.locator(".editor-tab.active", { hasText: targetName })).toBeVisible();
  });
});
