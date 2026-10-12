import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import type { FakeStep } from "../harness/fake-claude";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet } from "../harness/navigator";
import { openCommentedFile } from "../harness/pull-request";
import {
  deferredPhase,
  describeSample,
  frames,
  measureRender,
  type RenderSample,
  settle,
} from "../harness/render-budget";
import { appliedEdit } from "../harness/review";

// Render budgets for the review editor under every line decoration at once — keep/revert rows, review hint
// pills, PR threads, blame on every line — asserted as counts per input, never as time. Run: pnpm e2e:perf.
const FILES = 12;
const LINES = 120;
const STRIDE = 30;
const NOTCHES = 160;
const KEYS = 20;
const ENTERS = 5;
// Budgets sit 20-50% over the measured counts; one read per visible row per frame adds thousands. `deferred` is
// reads from debounced and observer callbacks, which only settle-paced steps make deterministic.
// Scrolling measured 1.01 reads per notch, all from mounting sections and their rows.
const READS_PER_NOTCH = 1.5;
// Per keystroke, measured 50 reads (+89 deferred) in the multi-file review, mostly re-rendering the hunks for
// autosave's echo, and 23 (+84) in the PR review. Per Enter in the last hunk, 95 (+205).
const REVIEW_KEY = { reads: 60, deferred: 110 };
const PR_KEY = { reads: 30, deferred: 105 };
const ENTER = { reads: 120, deferred: 260 };

const base = Array.from({ length: LINES }, (_, index) => `export const value${index} = ${index};`);
// Per 30 lines: a changed line plus an added one, and three deleted lines — eight hunks a file.
const edited = base.flatMap((line, index) =>
  index % STRIDE === 0
    ? [`export const value${index} = ${index} + 1;`, `export const added${index} = 0;`]
    : index % STRIDE >= 10 && index % STRIDE < 13
      ? []
      : [line],
);
const names = Array.from({ length: FILES }, (_, index) => `budget${index}.ts`);
const addedLines = edited.flatMap((line, index) => (line.includes("added") ? [index + 1] : []));
const blameEveryLine: FakeStep = {
  op: "mcp",
  tool: "setSetting",
  args: { key: "editor.gitBlame", value: "all" },
};
const findings: FakeStep = {
  op: "hook",
  request: {
    hook_event_name: "PostToolUse",
    tool_name: "ReportFindings",
    cwd: "{{WORKSPACE}}",
    tool_input: {
      findings: names.flatMap((file) =>
        addedLines.map((line) => ({
          file,
          line,
          summary: `Line ${line} changed`,
          verdict: "PLAUSIBLE",
        })),
      ),
    },
  },
};

test.describe.configure({ timeout: 180_000 });
// DOM snapshots read every node's scroll offsets on each action, through the probe's patched getters.
test.use({ trace: { mode: "retain-on-failure", snapshots: false }, preNavigate: deferredPhase });

// The Monaco editor holding the caret.
async function focusedEditor(page: Page) {
  return page.evaluateHandle(
    () => window.__WEAVIE_MONACO__!.editor.getEditors().find((editor) => editor.hasTextFocus())!,
  );
}

interface Budget {
  reads: number;
  deferred: number;
}

function expectWithin(sample: RenderSample, steps: number, budget: Budget): void {
  const report = describeSample(sample);
  expect(sample.reads, report).toBeLessThanOrEqual(steps * budget.reads);
  expect(sample.deferred, report).toBeLessThanOrEqual(steps * budget.deferred);
}

// Types `key` KEYS times on the caret's line: no content widget or zone card may be added or removed. Each key
// also refocuses the window, which refreshes PR comments, so the minute's refresh tick landing mid-run adds nothing
// new: the host publishes only changed comments (Refresh_PublishesOnlyWhenTheCommentsChanged).
async function expectTypingWithin(page: Page, key: string, perKey: Budget): Promise<void> {
  const sample = await measureRender(page, KEYS, KEYS * perKey.reads, async () => {
    await page.evaluate(() => window.dispatchEvent(new Event("focus")));
    await page.keyboard.type(key);
    await settle(page);
  });
  const report = describeSample(sample);
  expect(sample.added + sample.removed, report).toBe(0);
  expectWithin(sample, KEYS, perKey);
  expect(sample.reads, `the probe must still see Weavie's frames\n${report}`).toBeGreaterThan(0);
  const line = await (await focusedEditor(page)).evaluate((editor) =>
    editor.getModel()!.getLineContent(editor.getPosition()!.lineNumber),
  );
  expect(line, "every key must reach the buffer").toMatch(new RegExp(`${key}{${KEYS}}$`));
}

test.describe("a decorated multi-file review", () => {
  test.use({
    workspaceSeed: {
      run: async (workspace) => {
        await Promise.all(names.map((name) => writeFile(join(workspace, name), base.join("\n"))));
        execFileSync("git", ["add", "-A"], { cwd: workspace });
        execFileSync("git", ["commit", "-q", "-m", "budget baseline"], { cwd: workspace });
      },
    },
    fakeScript: {
      steps: [
        blameEveryLine,
        ...names.flatMap((name) => appliedEdit(name, edited.join("\n"))),
        findings,
      ],
    },
  });

  test.beforeEach(async ({ page }) => {
    await awaitReviewSet(page, names);
    await page.locator(".editor-empty-review").click();
    await expect(page.locator(".unified-review-file .weavie-hint-pill").first()).toBeVisible();
    await expect(page.locator(".unified-review-file .weavie-blame").first()).toBeVisible();
  });

  // Focuses the first file (clicking its first added line's left edge, clear of the hint pill) and puts the caret
  // at the end of added line `line`, in a hunk with keep/revert and a hint.
  async function caretAt(page: Page, line: number): Promise<void> {
    const section = page.locator(".unified-review-file").first();
    await section.locator(".view-line", { hasText: "added0" }).click({ position: { x: 2, y: 2 } });
    await (await focusedEditor(page)).evaluate((editor, lineNumber) => {
      editor.setPosition({ lineNumber, column: editor.getModel()!.getLineMaxColumn(lineNumber) });
      editor.revealLineInCenter(lineNumber);
    }, line);
    await settle(page);
  }

  test("scrolling reads no layout per frame and removes no widget @perf", async ({ page }) => {
    const bounds = (await page.locator(".unified-review-diffs").boundingBox())!;
    await page.mouse.move(bounds.x + bounds.width / 2, bounds.y + bounds.height / 2);
    await settle(page);
    const sample = await measureRender(page, NOTCHES, NOTCHES * READS_PER_NOTCH, async () => {
      await page.mouse.wheel(0, 120);
      await frames(page, 1);
    });
    const report = describeSample(sample);
    // A renamed bundle would drive the count to zero, passing while measuring nothing.
    expect(sample.reads, `the probe must still see Weavie's frames\n${report}`).toBeGreaterThan(0);
    expect(sample.reads, report).toBeLessThanOrEqual(NOTCHES * READS_PER_NOTCH);
    // Rows are added once as their editors mount; nothing already shown is rebuilt.
    expect(sample.added, `the probe must still see widgets mount\n${report}`).toBeGreaterThan(0);
    expect(sample.removed, report).toBe(0);
  });

  test("typing on a decorated line re-adds no widget @perf", async ({ page }) => {
    await caretAt(page, addedLines[0]!);
    await expectTypingWithin(page, "x", REVIEW_KEY);
  });

  // In the last hunk, so a re-add of every row (a dozen) dwarfs the few rows an Enter really moves.
  test("Enter re-adds only the line-end rows it moved @perf", async ({ page }) => {
    await caretAt(page, addedLines.at(-1)!);
    let moves = 0;
    const sample = await measureRender(page, ENTERS, ENTERS * ENTER.reads, async () => {
      // Rows below the caret shift down a line. The caret line's row is carried down by its tracked decoration
      // and re-anchored by the debounced placement, so it may move twice.
      moves += await (await focusedEditor(page)).evaluate((editor) => {
        const caret = editor.getPosition()!.lineNumber;
        const rows = [...editor.getDomNode()!.querySelectorAll("[widgetid^='weavie.lineEnd.']")];
        const line = (row: Element): number =>
          Number(row.getAttribute("widgetid")!.split(".").pop());
        return rows.reduce(
          (sum, row) => sum + (line(row) > caret ? 1 : line(row) === caret ? 2 : 0),
          0,
        );
      });
      await page.keyboard.press("Enter");
      await settle(page);
    });
    expectWithin(sample, ENTERS, ENTER);
    const report = describeSample(sample);
    expect(sample.added, report).toBeLessThanOrEqual(moves);
    expect(sample.removed, report).toBeLessThanOrEqual(moves);
  });
});

test.describe("a pull request review with threads", () => {
  test.use({ prScenario: true, fakeScript: { steps: [blameEveryLine] } });

  test("typing beside a PR thread re-adds no widget @perf", async ({ page }) => {
    await openCommentedFile(page);
    await page.locator(".monaco-editor .view-line", { hasText: "const message" }).first().click();
    await page.keyboard.press("End");
    await settle(page);
    await expectTypingWithin(page, " ", PR_KEY);
  });
});
