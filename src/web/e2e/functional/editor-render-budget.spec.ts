import { execFileSync } from "node:child_process";
import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { JSHandle, Page } from "@playwright/test";
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
// Typing is measured with the file filling the pane: the counts grow with the lines on screen.
const FILES = 12;
const LINES = 120;
const STRIDE = 30;
const NOTCHES = 160;
const KEYS = 20;
const ENTERS = 5;
// Budgets sit 20% over the measured counts; one read per visible row per frame adds thousands. `deferred` is
// reads from debounced and observer callbacks.

// Scrolling reads layout only to mount a file: the notches bring 4 files in at 40 reads (+21 deferred) each, and
// 3 on the way back at 38 (+15).
const FILE_MOUNT = { reads: 48, deferred: 26 };
const FILES_DOWN = 4;
const FILES_BACK = 3;
// A rest mounts the files ahead, so the notches that carry the review from its first file into its third build
// no editor: measured 29 reads (+0 deferred) for the whole scroll.
const RESTED_NOTCHES = 100;
const RESTED_SCROLL = { reads: 35, deferred: 0 };
// A jump builds its own files at once; the files ahead come after a rest, well past this.
const JUMP_BURST_MS = 150;
// Per keystroke, measured 14 reads (+0 deferred) in the multi-file review and 14 (+2) in the PR review, where the
// tab's unsaved marker comes and goes. Per Enter in the last hunk, 22 (+15).
const REVIEW_KEY = { reads: 17, deferred: 0 };
const PR_KEY = { reads: 17, deferred: 3 };
const ENTER = { reads: 27, deferred: 18 };

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

// When each Monaco editor from now on was created; a review file mounts one as it scrolls into view.
function createdEditors(page: Page): Promise<JSHandle<number[]>> {
  return page.evaluateHandle(() => {
    const times: number[] = [];
    window.__WEAVIE_MONACO__!.editor.onDidCreateEditor(() => times.push(performance.now()));
    return times;
  });
}

// Types `key` KEYS times on the caret's line: no content widget or zone card may be added or removed. Each key
// also refocuses the window, which refreshes PR comments, so the minute's refresh tick landing mid-run adds nothing
// new: the host publishes only changed comments (Refresh_PublishesOnlyWhenTheCommentsChanged).
async function expectTypingWithin(page: Page, key: string, perKey: Budget): Promise<RenderSample> {
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
  return sample;
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

  // Brings the first file to the top of the pane from the file list, focuses it (clicking clear of the hint
  // pill) and puts the caret at the end of added line `line`, in a hunk with keep/revert and a hint.
  async function caretAt(page: Page, line: number): Promise<void> {
    await page.locator(".unified-review-tree-row", { hasText: names[0]! }).click();
    await settle(page);
    const section = page.locator(".unified-review-file").first();
    await section.locator(".view-line", { hasText: "added0" }).click({ position: { x: 2, y: 2 } });
    await (await focusedEditor(page)).evaluate((editor, lineNumber) => {
      editor.setPosition({ lineNumber, column: editor.getModel()!.getLineMaxColumn(lineNumber) });
      editor.revealLineInCenter(lineNumber);
    }, line);
    await settle(page);
  }

  // Wheels NOTCHES notches of `deltaY`: layout is read only to mount the `files` that arrive, never per frame,
  // and nothing already shown is rebuilt or restyled.
  async function expectScrollWithin(page: Page, deltaY: number, files: number): Promise<void> {
    const created = await createdEditors(page);
    const sample = await measureRender(page, NOTCHES, FILES * FILE_MOUNT.reads, async () => {
      await page.mouse.wheel(0, deltaY);
      await frames(page, 1);
    });
    const report = describeSample(sample);
    // An editor built twice for one file would otherwise raise its own budget.
    expect(await created.jsonValue(), report).toHaveLength(files);
    expectWithin(sample, files, FILE_MOUNT);
    // A renamed bundle would drive the count to zero, passing while measuring nothing.
    expect(sample.reads, `the probe must still see Weavie's frames\n${report}`).toBeGreaterThan(0);
    // Rows are added once as their editors mount.
    expect(sample.added, `the probe must still see widgets mount\n${report}`).toBeGreaterThan(0);
    expect(sample.removed, report).toBe(0);
    expect(sample.restyles, report).toBe(0);
  }

  async function pointer(page: Page): Promise<[number, number]> {
    const bounds = (await page.locator(".unified-review-diffs").boundingBox())!;
    return [bounds.x + bounds.width / 2, bounds.y + bounds.height / 2];
  }

  async function hoverReview(page: Page): Promise<void> {
    await page.mouse.move(...(await pointer(page)));
    await settle(page);
  }

  test.describe("with no files mounted ahead", () => {
    test.use({ reviewFilesAhead: 0 });

    test("scrolling reads layout only to mount each file, down and back up @perf", async ({
      page,
    }) => {
      await hoverReview(page);
      await expectScrollWithin(page, 120, FILES_DOWN);
      await settle(page);
      await expectScrollWithin(page, -120, FILES_BACK);
    });
  });

  test("scrolling into the files a rest mounted builds no editor @perf", async ({ page }) => {
    await hoverReview(page);
    const created = await createdEditors(page);
    const sample = await measureRender(page, RESTED_NOTCHES, RESTED_SCROLL.reads, async () => {
      await page.mouse.wheel(0, 120);
      await frames(page, 1);
    });
    const report = describeSample(sample);
    // The file under the pointer is past the overscan the review started with.
    const reached = await page.evaluate(
      ([x, y]) => {
        const file = document
          .elementFromPoint(x!, y!)
          ?.closest<HTMLElement>(".unified-review-file");
        return file?.dataset.index;
      },
      await pointer(page),
    );
    expect(reached, report).toBe("2");
    expectWithin(sample, 1, RESTED_SCROLL);
    expect(await created.jsonValue(), report).toHaveLength(0);
    expect(sample.added + sample.removed, report).toBe(0);
    expect(sample.restyles, report).toBe(0);
  });

  // Load only widens the gaps between editors, so a slow machine cannot fail the first assertion.
  test("a jump from rest builds only the files at hand, then the files ahead @perf", async ({
    page,
  }) => {
    await hoverReview(page);
    const created = await createdEditors(page);
    await page.locator(".unified-review-tree-row", { hasText: names[8]! }).click();
    // The file jumped to and one either side, then two more each side as the review rests.
    await expect.poll(() => created.evaluate((times) => times.length)).toBe(7);
    const times = await created.jsonValue();
    expect(times.filter((time) => time - times[0]! < JUMP_BURST_MS).length).toBeLessThanOrEqual(3);
  });

  test("typing on a decorated line re-adds no widget @perf", async ({ page }) => {
    await caretAt(page, addedLines[0]!);
    const sample = await expectTypingWithin(page, "x", REVIEW_KEY);
    expect(sample.restyles, describeSample(sample)).toBe(0);
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
    expect(sample.restyles, report).toBe(0);
  });
});

test.describe("a pull request review with threads", () => {
  test.use({ prScenario: true, fakeScript: { steps: [blameEveryLine] } });

  // Stylesheet changes are not held to zero here: the hidden session's terminals drop WebGL on a timer, and the
  // renderer they fall back to adds its own style nodes.
  test("typing beside a PR thread re-adds no widget @perf", async ({ page }) => {
    await openCommentedFile(page);
    await page.locator(".monaco-editor .view-line", { hasText: "const message" }).first().click();
    await page.keyboard.press("End");
    await settle(page);
    await expectTypingWithin(page, " ", PR_KEY);
  });
});
