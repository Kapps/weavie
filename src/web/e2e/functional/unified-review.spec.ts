import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Locator, Page } from "@playwright/test";
import {
  activeSessionSlot,
  createSession,
  openFile,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
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
    await expect(overview.locator(".unified-review-heading")).toContainText("2 files changed");
    await expect(overview.locator(".unified-review-file-link")).toHaveCount(2);
    await expect(overview.locator(".unified-review-file")).toHaveCount(2);

    // The change is marked up in the editor itself (added band + removed ghost), not as hand-rolled rows.
    const hello = sectionFor(page, "hello.ts");
    await expect(hello.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });
    await expect(hello.locator(".weavie-inline-added").first()).toBeVisible();
    await expect(overview.locator(".unified-review-notice", { hasText: "Loading" })).toHaveCount(0);

    // …and it is tokenized: several distinct token classes, not one flat default run.
    await expect.poll(() => distinctTokenClasses(hello), { timeout: 15_000 }).toBeGreaterThan(2);

    await hello.locator(".unified-review-file-name").click();
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab", { hasText: "hello.ts" })).toBeVisible();
    await expect(page.locator(".weavie-inline-toolbar")).toBeVisible({ timeout: 15_000 });

    const mode = page.locator(".editor-review-toggle");
    await expect(mode).toHaveText("All changes");
    await mode.click();
    await expect(overview).toBeVisible();
    await expect(mode).toHaveText("File review");

    await openFile(page, "README.md");
    await expect(overview).toHaveCount(0);
    await expect(page.locator(".editor-tab.active", { hasText: "README.md" })).toBeVisible();
  });

  test("a file-level keep leaves the change in the faded reviewed band", async ({ page }) => {
    await page.locator(".editor-empty-review").click();
    const notes = sectionFor(page, "notes.txt");
    await expect(notes.locator(".unified-review-file-action.keep")).toBeVisible({
      timeout: 15_000,
    });

    await notes.locator(".unified-review-file-action.keep").click();

    await expect(notes.locator(".unified-review-status")).toHaveText("Reviewed", {
      timeout: 15_000,
    });
    await expect(notes.locator(".weavie-inline-accepted").first()).toBeVisible();
    await expect(notes.locator(".unified-review-file-action.keep")).toHaveCount(0);

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
    await expect(hello.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });
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
    await expect(notes.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });

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

test.describe("unified review mode — cross-session isolation", () => {
  const SHARED = "session content baseline\nline two\n";
  test.use({
    fakeScript: {
      // The same script reruns for every claude spawn, so the fork below lands an edit at the identical
      // relative path in its own worktree — the exact same-path-across-sessions shape the bug needed.
      steps: [...appliedEdit("shared.txt", SHARED)],
    },
  });

  // Regression for the cross-session model misroute: both sessions land a change at the same relative
  // path and both sit in Unified Review without ever explicitly leaving it, so switching between them
  // keeps App.tsx's `<Show when={mode() === "unified"}>` guard continuously true across the switch —
  // only the session-folded row key (UnifiedReview's getItemKey) can force Solid's keyed <For> to
  // unmount the shared "shared.txt" section instead of reusing its live editor for the new session.
  test("switching sessions that share a changed path rebinds the section instead of reusing it @cross", async ({
    page,
    weavie,
  }) => {
    test.slow();
    const chips = page.locator(".session-chip");

    // Session A: enter unified review for shared.txt and never leave it.
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".unified-review")).toBeVisible();
    let section = sectionFor(page, "shared.txt");
    await expect(section.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });

    // Fork session B (its own worktree); the same fake script lands the same-named change there.
    const primarySlot = await activeSessionSlot(page);
    await createSession(page, { branch: "e2e/unified-cross-session", provider: "claude" });
    await expect(chips).toHaveCount(2);
    const forkedSlot = await waitForSessionSwitch(page, primarySlot);

    // Session B also enters unified review for the same path.
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".unified-review")).toBeVisible();
    section = sectionFor(page, "shared.txt");
    await expect(section.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });

    const marker = `SESSION-B-MARKER-${Date.now()}`;
    await section.locator(".view-line", { hasText: "session content baseline" }).click();
    await page.keyboard.press("Home");
    await page.keyboard.type(marker);

    const [worktreeB] = sessionWorktrees(weavie.workspace);
    const forkedFile = join(worktreeB, "shared.txt");
    await expect
      .poll(() => readFile(forkedFile, "utf8").catch(() => ""), { timeout: 15_000 })
      .toContain(marker);

    // Switch back to session A. Its board is still unified (nothing resets an outgoing session's
    // mode), so the guard never toggles false across this switch — the fix is what has to force the
    // remount here.
    await chips.first().click();
    await waitForSessionSwitch(page, forkedSlot);
    await expect(page.locator(".unified-review")).toBeVisible();
    section = sectionFor(page, "shared.txt");
    await expect(section.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });

    // Session A's own model must show its own baseline — never session B's marker.
    await expect(section).not.toContainText(marker);
    const primaryFile = join(weavie.workspace, "shared.txt");
    await expect(readFile(primaryFile, "utf8")).resolves.not.toContain(marker);

    // Session B's edit landed only in its own worktree.
    const finalForked = await readFile(forkedFile, "utf8");
    expect(finalForked).toContain(marker);
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
    await expect(overview.locator(".monaco-editor")).toBeVisible({ timeout: 15_000 });
    await expect(overview).not.toContainText("Diff calculation timed out");
    await expect(overview.locator(".view-line", { hasText: "line 3999" })).toHaveCount(1);
  });
});

test.describe("unified review mode — large file set", () => {
  const fileCount = 100;
  test.use({
    fakeScript: {
      steps: Array.from({ length: fileCount }, (_, index) =>
        appliedEdit(`review-${String(index).padStart(3, "0")}.txt`, `change ${index}\n`),
      ).flat(),
    },
  });

  test("restores the exact file across a reverse mode toggle without mounting every editor", async ({
    page,
  }) => {
    await page.locator(".editor-empty-review").click();
    const overview = page.locator(".unified-review");
    const targetName = "review-099.txt";
    const targetLink = overview.locator(".unified-review-file-link", { hasText: targetName });
    await expect(overview.locator(".unified-review-file-link")).toHaveCount(fileCount);

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
    await expect(targetLink).toHaveClass(/active/);
    await expect(targetSection).toBeVisible();

    await overview.locator(".unified-review-action.mode").click();
    await expect(page.locator(".editor-tab.active", { hasText: targetName })).toBeVisible();
  });
});
