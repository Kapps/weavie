import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { navChord } from "../harness/navigator";
import { appliedEdit } from "../harness/review";

// The POST-TURN review surface (applied changes), keep/revert/undo/redo, the parked navigator, and the
// keyboard-fall-through regressions. Distinct from diff.spec.ts, which exercises the openDiff PROPOSAL seam.
// Applied changes are hook-driven (appliedEdit), so these run the whole stack: fake → hook bridge → tracker →
// turn-diff push → inline toolbar → keep/revert → Core → disk.

// hello.ts is seeded (git-workspace.ts) as the greet() function; two separated edits (lines 2 and 6) give two
// independent hunks for the walk.
const TWO_HUNKS =
  "export function greet(name: string): string {\n" +
  "  return `Hi there, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";

const read = (workspace: string, rel: string): string => readFileSync(join(workspace, rel), "utf8");

// The live applied toolbar carries the scope picker; the parked navigator doesn't — so this asserts "a live
// review is rendered over the active file" specifically (not just any toolbar).
const SCOPE = ".weavie-inline-scope";
const ADDED = ".weavie-inline-added"; // one decoration per BRIGHT (pending) changed line → one per single-line hunk
const ACCEPTED = ".weavie-inline-accepted"; // one decoration per FADED (kept-but-uncommitted) line
const UNDO = ".weavie-inline-accepted-undo"; // the inline ↶ undo beside a faded hunk
const KEEP = ".weavie-inline-pending-keep"; // the inline ✓ keep beside a bright pending hunk
const REVERT = ".weavie-inline-pending-revert"; // the inline ✕ revert beside a bright pending hunk
const HIST_UNDO = ".weavie-inline-hist"; // the toolbar's ↶ Undo (first) / ↷ Redo history buttons
const TOOLBAR = ".weavie-inline-toolbar";

// Land the caret on the first hunk deterministically (next-change from the top of the file), so a per-hunk
// keep/revert acts on a known hunk regardless of where the file opened.
async function focusFirstHunk(page: import("@playwright/test").Page): Promise<void> {
  await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press(navChord("ArrowDown"));
}

// The caret line as the real editor reports it (window.__WEAVIE_EDITOR__ is the IStandaloneCodeEditor).
const caretLine = (page: import("@playwright/test").Page): Promise<number | null> =>
  page.evaluate(
    () =>
      (
        window as Window & {
          __WEAVIE_EDITOR__?: { getPosition(): { lineNumber: number } | null };
        }
      ).__WEAVIE_EDITOR__?.getPosition()?.lineNumber ?? null,
  );

test.describe("applied review — keep & undo", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("keeping a hunk drops only it from the diff; undo brings it back", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2); // two hunks pending

    // Keep at scope = Change (default): the hunk at the caret leaves the diff, the other stays.
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1);

    // Undo the keep — the hunk returns to the pending set.
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    await expect(page.locator(ADDED)).toHaveCount(2);
  });
});

test.describe("applied review — resolving the last change exits the review", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("keeping every hunk one-by-one dismisses the review without keep-all", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Keep hunk 1 at scope = Change: the review stays up (hunk 2 is still bright, hunk 1 fades).
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1);
    await expect(page.locator(SCOPE)).toBeVisible();

    // Keep the LAST hunk: the review settles, commits (as keep-all would), and exits — no lingering
    // toolbar with every change already approved, and the faded bands clear with the commit.
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(ACCEPTED)).toHaveCount(0);
  });
});

test.describe("applied review — undo-keep reveals the restored hunk", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  // hunk 1 is the greeting (line 2, Hello→Hi there); hunk 2 is the call (line 6, console.log→console.warn).
  test("undoing a keep lands the editor back on the re-pended first hunk", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Land on hunk 1 (line 2) and Keep it — the caret advances toward hunk 2, leaving line 2.
    await focusFirstHunk(page);
    await expect.poll(() => caretLine(page)).toBe(2);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1); // hunk 1 kept
    await expect.poll(() => caretLine(page)).toBeGreaterThan(2); // caret moved off hunk 1

    // Undo the keep — the host re-pends hunk 1 AND reveals it, landing the editor back on line 2.
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    await expect(page.locator(ADDED)).toHaveCount(2); // hunk 1 re-pended
    await expect.poll(() => caretLine(page)).toBe(2); // editor revealed the restored hunk
  });

  // The reveal must land on the hunk the undo ACTED on — it only coincides with the file's first pending
  // hunk when the undone keep was the first hunk (the test above).
  test("undoing a keep of the second hunk lands on it, not the file's first hunk", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Land on hunk 2 (line 6) and Keep it — the walk wraps the caret back to hunk 1.
    await focusFirstHunk(page);
    await page.keyboard.press(navChord("ArrowDown"));
    await expect.poll(() => caretLine(page)).toBe(6);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1);
    await expect.poll(() => caretLine(page)).toBe(2);

    // Undo the keep — the editor lands on the restored hunk 2, not the still-pending hunk 1.
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    await expect(page.locator(ADDED)).toHaveCount(2);
    await expect.poll(() => caretLine(page)).toBe(6);
  });
});

// The review position tracks what's ON SCREEN: it keys to the cursor only while the cursor is in view; a
// manual scroll moves it with the viewport, so the counter and Keep/Revert act on the visible hunk — never
// on a hunk the caret was parked on before the scroll (which Keep would then silently act on and jump to).
test.describe("applied review — manual scrolling retargets the review position", () => {
  // long.ts is seeded (git-workspace.ts) as 160 comment lines; editing lines 2 and 110 gives two hunks more
  // than a viewport apart, so scrolling to one puts the other (and a caret parked on it) off-screen.
  const longEdit = (): string => {
    const lines = Array.from({ length: 160 }, (_, i) => `// line ${i + 1}`);
    lines[1] = "// line 2 EDITED";
    lines[109] = "// line 110 EDITED";
    return `${lines.join("\n")}\n`;
  };
  test.use({ fakeScript: { steps: [...appliedEdit("long.ts", longEdit())] } });

  // Asserts ride the toolbar counter (computed from the full hunk set), not decoration counts — Monaco
  // virtualizes the view, so an off-screen hunk's decorations aren't in the DOM at all.
  test("scrolling away moves the counter and Keep to the visible hunk", async ({ page }) => {
    await openFile(page, "long.ts");
    await focusFirstHunk(page); // caret on hunk 1 (line 2)
    await expect.poll(() => caretLine(page)).toBe(2);
    const counter = page.locator(".weavie-inline-stack-sub");
    await expect(counter).toContainText("change 1/2");

    // Scroll to the bottom without touching the caret — the position follows the viewport to hunk 2.
    await page.evaluate(() => {
      const editor = (
        window as Window & {
          __WEAVIE_EDITOR__?: { setScrollTop(top: number): void; getScrollHeight(): number };
        }
      ).__WEAVIE_EDITOR__;
      editor?.setScrollTop(editor.getScrollHeight());
    });
    await expect(counter).toContainText("change 2/2");
    await expect.poll(() => caretLine(page)).toBe(2); // the caret itself never moved

    // Keep acts on the visible hunk 2 (fading it), then walks to the remaining hunk 1 back at the top.
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(counter).toContainText("change 1/1");
    await expect.poll(() => caretLine(page)).toBe(2);
    await expect(page.locator(ADDED)).toHaveCount(1); // hunk 1, revealed at the top, is still bright
  });
});

test.describe("applied review — accepted band fades (kept, not vanished) + inline undo", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("keeping a hunk fades it with an inline ↶ undo that re-pends it", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2); // two bright pending hunks
    await expect(page.locator(ACCEPTED)).toHaveCount(0); // nothing kept yet

    // Keep the first hunk: it stays VISIBLE but faded — proof it's accepted — with an inline ↶ undo beside it.
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1); // one bright hunk remains
    await expect(page.locator(ACCEPTED)).toHaveCount(1); // the kept hunk is now faded, not gone
    await expect(page.locator(UNDO)).toHaveCount(1); // its inline ↶ undo

    // Click the inline undo: the kept hunk returns to the bright pending band (no disk write — it never moved disk).
    await page.locator(UNDO).click();
    await expect(page.locator(ADDED)).toHaveCount(2);
    await expect(page.locator(ACCEPTED)).toHaveCount(0);
  });

  test("keep-all clears both the pending and the faded accepted band", async ({ page }) => {
    await openFile(page, "hello.ts");
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter"); // keep hunk 1 → it fades, leaving one pending + one accepted
    await expect(page.locator(ACCEPTED)).toHaveCount(1);

    // Keep-all is the commit point: the accepted anchor snaps to current, so EVERY marker clears (bright + faded).
    await runCommand(page, "Keep All Changes");
    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(ACCEPTED)).toHaveCount(0);
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
  });
});

test.describe("applied review — a new turn commits the faded accepted band", () => {
  // The fake pauses after its edits until the test signals (waitFile), then submits a new prompt — the
  // UserPromptSubmit hook is the turn-start boundary that implicitly commits whatever was kept.
  const SIGNAL = ".weavie-e2e-turn-signal";
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", TWO_HUNKS),
        { op: "waitFile", path: `{{WORKSPACE}}/${SIGNAL}` },
        { op: "hook", request: { hook_event_name: "UserPromptSubmit" } },
      ],
    },
  });

  test("kept hunks disappear from the diff at the next prompt; pending ones stay", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "hello.ts");
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter"); // keep hunk 1 → faded, hunk 2 still bright
    await expect(page.locator(ACCEPTED)).toHaveCount(1);
    await expect(page.locator(ADDED)).toHaveCount(1);

    // Signal the fake to submit its next prompt: the turn boundary commits the kept hunk out of the view.
    writeFileSync(join(weavie.workspace, SIGNAL), "");
    await expect(page.locator(ACCEPTED)).toHaveCount(0); // the faded band is gone — committed
    await expect(page.locator(UNDO)).toHaveCount(0); // and its inline ↶ undo with it
    await expect(page.locator(ADDED)).toHaveCount(1); // the unreviewed hunk still accumulates
  });
});

test.describe("applied review — inline ✓ keep / ✕ revert on pending hunks", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("every pending hunk carries its own keep/revert; clicking ✓ keep fades just that hunk", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);
    await expect(page.locator(KEEP)).toHaveCount(2); // one ✓ keep / ✕ revert pair per pending hunk
    await expect(page.locator(REVERT)).toHaveCount(2);

    // Click the first hunk's ✓ keep: it fades (with its ↶ undo), the other stays pending with its buttons.
    await page.locator(KEEP).first().click();
    await expect(page.locator(ADDED)).toHaveCount(1);
    await expect(page.locator(ACCEPTED)).toHaveCount(1);
    await expect(page.locator(UNDO)).toHaveCount(1);
    await expect(page.locator(KEEP)).toHaveCount(1);
  });

  test("clicking ✕ revert rewrites disk for just that hunk", async ({ page, weavie }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(REVERT)).toHaveCount(2);

    // Revert the first hunk (the greet() line): its baseline returns to disk, the other hunk is untouched.
    await page.locator(REVERT).first().click();
    await expect.poll(() => read(weavie.workspace, "hello.ts")).toContain("Hello, ${name}");
    expect(read(weavie.workspace, "hello.ts")).toContain("console.warn");
    await expect(page.locator(ADDED)).toHaveCount(1);
    await expect(page.locator(REVERT)).toHaveCount(1);
  });
});

test.describe("applied review — revert & undo-revert (disk)", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("reverting a hunk rewrites disk; undo-revert restores it", async ({ page, weavie }) => {
    await openFile(page, "hello.ts");
    await focusFirstHunk(page); // caret on the greet() line (Hello → Hi there)

    // Revert the hunk: Core rewrites the file back to its baseline line.
    await page.keyboard.press("ControlOrMeta+Backspace");
    await expect.poll(() => read(weavie.workspace, "hello.ts")).toContain("Hello, ${name}"); // baseline line is back on disk
    expect(read(weavie.workspace, "hello.ts")).toContain("console.warn"); // the other hunk is untouched

    // The revert writes disk INSIDE Core, before its turn-diff/review-history messages reach the page — so
    // syncing on disk alone races the undo: Ctrl+Shift+Backspace would consume the key but no-op while the
    // client's canUndoRevert is still false. Wait for the web to reflect the revert before undoing it.
    await expect(page.locator(ADDED)).toHaveCount(1); // the reverted hunk left the bright band
    await expect(page.locator(HIST_UNDO).first()).toBeEnabled(); // the revert is now undoable on the client

    // Undo the revert: the change is rewritten to disk.
    await page.keyboard.press("ControlOrMeta+Shift+Backspace");
    await expect.poll(() => read(weavie.workspace, "hello.ts")).toContain("Hi there, ${name}");
  });
});

test.describe("applied review — Shift+Enter never types into the file (regression)", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  // The bug: with nothing kept, undoKeep declined and the chord fell through to Monaco, which inserted a
  // newline INTO the file under review — corrupting it and mismatching the next keep/revert's guard.
  test("Ctrl+Shift+Enter with nothing to undo leaves the file byte-for-byte unchanged", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });
    const before = read(weavie.workspace, "hello.ts");

    // Focus the editor (so a fall-through really would type) and mash the undo chords.
    await page.locator(".monaco-editor .view-lines").first().click();
    for (let i = 0; i < 4; i++) {
      await page.keyboard.press("ControlOrMeta+Shift+Enter");
      await page.keyboard.press("ControlOrMeta+Shift+Backspace");
    }
    // The bug inserts a newline straight into the editor MODEL (which then autosaves). Reading the model is the
    // immediate, deterministic signal — no autosave round-trip to wait on — and it must still equal the
    // freshly-opened baseline...
    const modelText = await page.evaluate(
      () =>
        (
          window as Window & { __WEAVIE_EDITOR__?: { getModel(): { getValue(): string } | null } }
        ).__WEAVIE_EDITOR__
          ?.getModel()
          ?.getValue() ?? null,
    );
    expect(modelText).toBe(before);
    // ...so with no edit ever made, disk is byte-for-byte untouched too.
    expect(read(weavie.workspace, "hello.ts")).toBe(before);
  });
});

test.describe("applied review — scope picker (keep whole file)", () => {
  // A second changed file keeps the review unsettled: File-scope keep must FADE the kept file, not commit
  // the review (with no other pending file, keeping the last one settles and exits — covered above).
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", TWO_HUNKS),
        ...appliedEdit("notes.txt", "just plain text\nand a second changed line\n"),
      ],
    },
  });

  test("with scope = File, one Keep fades every hunk in the file (kept, not gone)", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Pick "This file" in the sticky scope dropdown, then Keep once.
    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
    await page.locator(".weavie-inline-accept").click();

    // No pending hunks remain HERE, but the whole file is now faded-accepted (both hunks) with their inline
    // undos — a fully-kept file still renders its faded band while another file is pending.
    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(ACCEPTED)).toHaveCount(2);
    await expect(page.locator(UNDO)).toHaveCount(2);
  });
});

test.describe("applied review — scope picker (keep all, single file)", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  // A single-file review has no ← / → file axis, so "All files" reads as "All changes" — but it must still be
  // offered, because keep-all is the only toolbar scope that commits the review and closes the navigator.
  test("with scope = All changes, one Keep commits the single-file review and closes the toolbar", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "All changes" }).click();
    await page.locator(".weavie-inline-accept").click();

    // Committed: every marker (bright + faded) clears and the toolbar leaves — the review is fully closed.
    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(ACCEPTED)).toHaveCount(0);
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
  });
});

test.describe("parked navigator — surfaces without moving the editor", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("a pending review parks over an unrelated file; a nav key steps in", async ({ page }) => {
    // Open an UNCHANGED file: the review is non-empty, so the toolbar parks over it (editor untouched).
    await openFile(page, "README.md");
    const sub = page.locator(".weavie-inline-stack-sub");
    await expect(sub).toContainText("press ↓", { timeout: 15_000 });
    await expect(page.locator(SCOPE)).toHaveCount(0); // parked: no scope picker yet
    await expect(page.locator(".weavie-inline-accept")).toBeDisabled(); // Keep is inert while parked

    // Step in — opens the first changed file at its first hunk; the live toolbar (scope picker) takes over.
    await page.keyboard.press(navChord("ArrowDown"));
    await expect(page.locator(SCOPE)).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines")).toContainText("Hi there");
  });
});

test.describe("applied review — keep-all commits the set", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("keep-all clears the review surface", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Keep-all via the palette (the commit point): the marks clear and the toolbar leaves.
    await runCommand(page, "Keep All Changes");

    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
  });
});

test.describe("multi-file review walk", () => {
  test.use({
    fakeScript: {
      steps: [
        ...appliedEdit("hello.ts", TWO_HUNKS),
        ...appliedEdit("notes.txt", "just plain text\nand a second changed line\n"),
      ],
    },
  });

  test("the parked navigator counts every changed file", async ({ page }) => {
    await openFile(page, "README.md"); // unchanged → parks
    await expect(page.locator(".weavie-inline-stack-sub")).toContainText("2 files", {
      timeout: 15_000,
    });
    // ← / → file buttons render for a multi-file review.
    await expect(page.locator(".weavie-inline-file")).toHaveCount(2);
  });

  // Keeping the last bright hunk of a file fades it but the file stays in the review set (faded band), so the
  // host's re-emit won't advance — Keep must step to the next file itself, or the walk strands on a file with
  // nothing left to review.
  test("keeping the last change in a file advances to the next file", async ({ page }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2); // two bright pending hunks

    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter"); // keep hunk 1 → fades; caret lands on hunk 2
    await expect(page.locator(ADDED)).toHaveCount(1);

    await page.keyboard.press("ControlOrMeta+Enter"); // keep the last bright hunk → advance to the next file
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("notes.txt", {
      timeout: 15_000,
    });
  });

  // Same strand on revert: once a hunk is kept (faded band present), reverting the file's last bright hunk
  // leaves acceptedBaseline != current, so the host's re-emit won't advance — revert must step on itself.
  test("reverting the last pending change after a keep advances to the next file", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter"); // keep hunk 1 → fades; caret lands on hunk 2
    await expect(page.locator(ACCEPTED)).toHaveCount(1); // a faded band now exists
    await expect(page.locator(ADDED)).toHaveCount(1); // one bright hunk remains

    await page.keyboard.press("ControlOrMeta+Backspace"); // revert the last bright hunk → advance to next file
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("notes.txt", {
      timeout: 15_000,
    });
  });
});

// A contiguous multi-line change must read as ONE solid green block. Regression: the char-level highlight was
// an inlineClassName, whose background stops at the font's text box — a light seam showed between every pair
// of adjacent added lines. As a className overlay it fills each line's full height, so the seams vanish.
test.describe("applied review — a multi-line change is one solid block", () => {
  const BLOCK_EDIT =
    "export function greet(name: string): string {\n" +
    "  const prefix = `Hi`;\n" +
    "  const suffix = `!!`;\n" +
    "  return `${prefix} there, ${name}${suffix}`;\n" +
    "}\n\n" +
    'const message = greet("weavie");\n' +
    "console.log(message);\n";
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", BLOCK_EDIT)] } });

  test("the char-level highlight fills each line's full height (no seam between lines)", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(3); // one hunk spanning three added lines
    // Every char-level overlay must be exactly as tall as the whole-line wash (always full line height) —
    // any shortfall is the seam. Measured against the wash, not parentElement, whose height depends on
    // inline-layout quirks under the buggy rendering.
    const heights = await page.evaluate(() => ({
      line: (document.querySelector(".weavie-inline-added") as HTMLElement).getBoundingClientRect()
        .height,
      overlays: [...document.querySelectorAll(".weavie-inline-added-text")].map(
        (el) => el.getBoundingClientRect().height,
      ),
    }));
    expect(heights.overlays.length).toBeGreaterThan(0);
    expect(heights.overlays).toEqual(heights.overlays.map(() => heights.line));
  });
});

// A brand-new file (empty baseline → every line "added") renders calmly: a "New file" band + the single gutter
// edge, NOT the per-line green wash a modified file gets. brand-new.ts is absent from the seed set, so its
// baseline is empty; hello.ts is seeded, so it stays a normal modified diff.
test.describe("applied review — a new file is marked, not washed", () => {
  const NEW_CONTENT =
    "export const answer = 42;\n" +
    "export function double(): number {\n" +
    "  return answer * 2;\n" +
    "}\n";
  const NEWFILE_TAG = ".weavie-inline-newfile-tag";
  const GUTTER = ".weavie-inline-added-gutter";
  test.use({
    fakeScript: {
      steps: [...appliedEdit("brand-new.ts", NEW_CONTENT), ...appliedEdit("hello.ts", TWO_HUNKS)],
    },
  });

  test("a new file shows the New file band and no per-line wash; a modified file still washes", async ({
    page,
  }) => {
    await openFile(page, "brand-new.ts");
    // Labelled once, with the continuous gutter edge — but none of the per-line green wash.
    await expect(page.locator(NEWFILE_TAG)).toHaveText("New file");
    await expect(page.locator(GUTTER).first()).toBeVisible();
    await expect(page.locator(ADDED)).toHaveCount(0);

    // The modified file is untouched by the change: every changed line still washes, and there's no New file band.
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);
    await expect(page.locator(NEWFILE_TAG)).toHaveCount(0);
  });
});
