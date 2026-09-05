import { readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { awaitReviewSet, navChord, walkToChangedFile } from "../harness/navigator";
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

const fourHunks = (): string => {
  const lines = Array.from({ length: 160 }, (_, i) => `// line ${i + 1}`);
  for (const line of [2, 40, 80, 120]) {
    lines[line - 1] += " EDITED";
  }
  return `${lines.join("\n")}\n`;
};

// The live applied toolbar carries the scope picker; the parked navigator doesn't — so this asserts "a live
// review is rendered over the active file" specifically (not just any toolbar).
const SCOPE = ".weavie-inline-scope";
const ADDED = ".weavie-inline-added"; // one decoration per consecutive BRIGHT (pending) styling run
const ACCEPTED = ".weavie-inline-accepted"; // one decoration per consecutive FADED (kept) run
const UNDO = ".weavie-inline-accepted-undo"; // the inline ↶ undo beside a faded hunk
const KEEP = ".weavie-inline-pending-keep"; // the inline ✓ keep beside a bright pending hunk
const REVERT = ".weavie-inline-pending-revert"; // the inline ✕ revert beside a bright pending hunk
const HIST_UNDO = ".weavie-inline-hist"; // the toolbar's ↶ Undo (first) / ↷ Redo history buttons
const TOOLBAR = ".weavie-inline-toolbar";
const KEEP_BTN = ".weavie-inline-toolbar .weavie-inline-accept"; // the toolbar's scope-driven Keep

const decorationCount = (
  page: import("@playwright/test").Page,
  className: string,
): Promise<number> =>
  page.evaluate(
    (name) =>
      (
        window as Window & {
          __WEAVIE_EDITOR__?: {
            getModel(): {
              getAllDecorations(): { options: { className: string | null } }[];
            } | null;
          };
        }
      ).__WEAVIE_EDITOR__
        ?.getModel()
        ?.getAllDecorations()
        .filter((decoration) => decoration.options.className === name).length ?? 0,
    className,
  );

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

  // Flaked on windows-latest 2026-08-05 04:33 UTC, stuck at count 1 for the full 30s expect.timeout:
  // https://github.com/Kapps/weavie/actions/runs/30975495342/job/92209636984. Fixed in HostCore.WebBridge's
  // ApplyHistoryResult (see its doc comment) by ordering the "history" push before "diff"/"changes".
  test("keeping a hunk drops only it from the diff; undo brings it back", async ({ page }) => {
    // Flaked on windows-latest 2026-08-05 04:33 UTC: the undo's re-pend (hook bridge → Core →
    // rewrite hello.ts → re-render) never resolved within the 30s Windows expect.timeout, unrelated
    // to the PR that surfaced it (a turn-navigation change) — the very next CI run passed this same
    // test untouched: https://github.com/Kapps/weavie/actions/runs/30975495342/job/92208988130
    // test.slow triples the overall timeout budget for the keep+undo round trip; not a retry.
    test.slow();
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2); // two hunks pending

    // Keep at scope = Change (default): the hunk at the caret leaves the diff, the other stays.
    await focusFirstHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(ADDED)).toHaveCount(1);

    // The keep's history availability (canUndoKeep) is host-pushed, arriving after the local decoration
    // update above — pressing the undo chord before it lands consumes the key but no-ops (same race as the
    // revert/undo-revert test below). Wait for the toolbar's history undo to reflect it first.
    // Flaked 2026-08-06 (run https://github.com/Kapps/weavie/actions/runs/30975495342, e2e windows shard
    // 2/6): undo-keep chord raced the host round-trip, so `.weavie-inline-added` stayed at 1. Fixed by
    // waiting for HIST_UNDO to be enabled before firing the chord.
    await expect(page.locator(HIST_UNDO).first()).toBeEnabled();

    // Undo the keep — the hunk returns to the pending set.
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    await expect(page.locator(ADDED)).toHaveCount(2);
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

    // Wait for the host-pushed canUndoKeep before undoing (see the race noted in the test above).
    await expect(page.locator(HIST_UNDO).first()).toBeEnabled();

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

    // Wait for the host-pushed canUndoKeep before undoing (see the race noted in the first test above).
    await expect(page.locator(HIST_UNDO).first()).toBeEnabled();

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
    await clickIntoEditor(page);
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

test.describe("applied review — typing never tears the diff down (regression)", () => {
  // long.ts is 160 seeded lines with hunks at 2/40/80/120 — long enough that the removed-line ghost zones
  // hold real height above the caret, so losing them shows up as the document visibly moving.
  test.use({ fakeScript: { steps: [...appliedEdit("long.ts", fourHunks())] } });

  // The bug: a recompute repainted a bare "Updating diff…" bar — dropping every decoration, every removed-line
  // ghost zone and the whole toolbar — and it fired on each keystroke AND on each 250ms autosave's re-pushed
  // diff. With the zones gone the lines below them slide up, and the next render drops them back, so the text
  // jumped under the caret several times a second.
  test("edits keep the highlights and toolbar up, and never move the text under the caret", async ({
    page,
  }) => {
    await openFile(page, "long.ts");
    await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(4);

    // Sit below the last hunk with room to spare, caret on line 140 (untouched by the agent). Line 125 is
    // then the anchor: below all four ghost zones, above the edit, and clear of the viewport edges so no
    // caret reveal can move it. Only losing a ghost zone can.
    await page.evaluate(() => {
      const editor = window.__WEAVIE_EDITOR__;
      const lineHeight = editor?.getOption(window.__WEAVIE_MONACO__.editor.EditorOption.lineHeight);
      if (editor === undefined || lineHeight === undefined) {
        throw new Error("editor not ready");
      }
      editor.focus();
      // setScrollTop, not revealLine — a reveal animates, and the probe below must start from a settled view.
      editor.setScrollTop(100 * lineHeight);
      editor.setPosition({ lineNumber: 140, column: 1 });
    });

    // Sample every frame. delay 150 > the 120ms recompute debounce, so each keystroke matures its own full
    // recompute rather than one at the end, and the 250ms autosave re-pushes land in between.
    await page.evaluate(() => {
      const editor = window.__WEAVIE_EDITOR__;
      // The anchor's offset in DOCUMENT space (viewport top + scroll), so ordinary scrolling cancels out and
      // only a change in the zone heights ABOVE line 125 — i.e. the ghosts vanishing — can move it.
      const anchorTop = (): number =>
        (editor?.getScrolledVisiblePosition({ lineNumber: 125, column: 1 })?.top ?? Number.NaN) +
        (editor?.getScrollTop() ?? 0);
      const probe = { added: 99, toolbarMissing: 0, anchorMoved: 0, frames: 0 };
      (window as Window & { __DIFF_PROBE__?: typeof probe }).__DIFF_PROBE__ = probe;
      const from = anchorTop();
      const tick = (): void => {
        probe.frames++;
        probe.added = Math.min(
          probe.added,
          (
            editor?.getModel() as unknown as {
              getAllDecorations(): { options: { className: string } }[];
            }
          )
            ?.getAllDecorations()
            .filter((decoration) => decoration.options.className === "weavie-inline-added")
            .length ?? 0,
        );
        if (document.querySelector(".weavie-inline-scope") === null) {
          probe.toolbarMissing++;
        }
        if (anchorTop() !== from) {
          probe.anchorMoved++;
        }
        requestAnimationFrame(tick);
      };
      requestAnimationFrame(tick);
    });

    // Line 140 is untouched by the agent, so this lands as a USER change (weavie-inline-user) below the
    // anchor, without disturbing any pending hunk — and it's the deterministic "the recompute landed" signal.
    await page.keyboard.type("// noted!!", { delay: 150 });
    await expect(page.locator(".weavie-inline-user")).toHaveCount(1);
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(4);

    const probe = await page.evaluate(
      () => (window as Window & { __DIFF_PROBE__?: Record<string, number> }).__DIFF_PROBE__,
    );
    expect(probe?.frames, "the sampler never ran").toBeGreaterThan(10);
    expect(probe?.added, "highlights were cleared mid-edit").toBe(4);
    expect(probe?.toolbarMissing, "the review toolbar was replaced mid-edit").toBe(0);
    expect(probe?.anchorMoved, "the document moved under the caret while typing").toBe(0);
  });
});

test.describe("applied review — scope picker (keep whole file)", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", TWO_HUNKS)] } });

  test("with scope = File, one Keep fades every hunk in the file (kept, not gone)", async ({
    page,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(2);

    // Pick "This file" in the sticky scope dropdown, then Keep once.
    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
    await page.locator(".weavie-inline-accept").click();

    // No pending hunks remain, but the whole file is now faded-accepted (both hunks) with their inline undos —
    // a fully-kept file still renders its faded band (it isn't bailed on for having no bright diff).
    await expect(page.locator(ADDED)).toHaveCount(0);
    await expect(page.locator(ACCEPTED)).toHaveCount(2);
    await expect(page.locator(UNDO)).toHaveCount(2);
  });

  // A single-file review has no ← / → file axis, so "All files" reads as "All changes" — but it must still be
  // offered, because keep-all is the only toolbar scope that commits the review and closes the navigator.
  // Without it a one-file review could only ever be faded (kept-but-uncommitted), never dismissed.
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

test.describe("applied review — file scope from a later change", () => {
  const changed = fourHunks();
  test.use({ fakeScript: { steps: [...appliedEdit("long.ts", changed)] } });

  test("keeping the file from change 4 preserves its already-applied bytes and fades every hunk", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "long.ts");
    await focusFirstHunk(page);
    const counter = page.locator(".weavie-inline-stack-sub");
    await expect(counter).toContainText("change 1/4");
    for (let i = 0; i < 3; i++) {
      await page.keyboard.press(navChord("ArrowDown"));
    }
    await expect(counter).toContainText("change 4/4");

    // Applied review observes edits that are already in the live file. Keep changes review state, not bytes.
    expect(read(weavie.workspace, "long.ts")).toBe(changed);

    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "This file" }).click();
    await page.locator(".weavie-inline-accept").click();

    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(0);
    await expect.poll(() => decorationCount(page, "weavie-inline-accepted")).toBe(4);
    expect(read(weavie.workspace, "long.ts")).toBe(changed);
    await expect
      .poll(() =>
        page.evaluate(
          () =>
            (
              window as Window & {
                __WEAVIE_EDITOR__?: { getModel(): { getValue(): string } | null };
              }
            ).__WEAVIE_EDITOR__
              ?.getModel()
              ?.getValue() ?? null,
        ),
      )
      .toBe(changed);
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

// A created file's whole content IS the change, so its first change is line 1. The review walk used to express
// that as "open at line 1", which the tab store read as "no target" and answered with the tab's saved scroll —
// landing the user wherever they last were in the file instead of on the change.
test.describe("applied review — walking to a new file lands on its top", () => {
  const LONG_NEW = `${Array.from({ length: 400 }, (_, i) => `export const v${i + 1} = ${i + 1};`).join("\n")}\n`;
  test.use({
    fakeScript: {
      steps: [...appliedEdit("brand-new.ts", LONG_NEW), ...appliedEdit("hello.ts", TWO_HUNKS)],
    },
  });

  test("stepping back to a created file reveals line 1, not the saved scroll", async ({ page }) => {
    await openFile(page, "brand-new.ts");
    await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });

    // Read deep into the new file, then leave it — the tab remembers this position.
    await page.evaluate(() => {
      window.__WEAVIE_EDITOR__?.setPosition({ lineNumber: 300, column: 1 });
      window.__WEAVIE_EDITOR__?.revealLineInCenter(300);
    });
    await expect.poll(() => caretLine(page)).toBe(300);
    await openFile(page, "hello.ts");
    await expect(page.locator(".weavie-inline-stack-name")).toHaveText("hello.ts");

    // Walk the review's file axis back onto the created file: it must open on its change (the top).
    await walkToChangedFile(page, "brand-new.ts");
    await expect.poll(() => caretLine(page)).toBe(1);
    await expect.poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getScrollTop())).toBe(0);
  });
});

test.describe("applied review — large files stay responsive", () => {
  const TYPING_HEARTBEAT_BUDGET_MS = 230;
  const original = Array.from({ length: 5_000 }, (_, index) => `old line ${index}`).join("\n");
  const modified = Array.from({ length: 5_000 }, (_, index) => `new line ${index}`).join("\n");
  test.use({
    fakeScript: {
      steps: [
        { op: "edit", path: "{{WORKSPACE}}/generated.txt", content: original },
        ...appliedEdit("generated.txt", modified),
      ],
    },
  });

  test("renders and reviews a 5,000-line rewrite with bounded editor allocations", async ({
    page,
  }) => {
    await openFile(page, "generated.txt");
    await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(TOOLBAR)).not.toContainText("timed out");
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(1);
    await expect(page.locator(".weavie-inline-removed-line")).toHaveCount(1);
    await expect(page.locator(".weavie-inline-removed-line")).toHaveAttribute(
      "data-line-count",
      "5000",
    );
    await awaitReviewSet(page, ["generated.txt"]);
    await page.evaluate(() => window.__WEAVIE_EDITOR__?.setScrollTop(0));
    const ghostContent = page.locator(".weavie-inline-removed-content");
    const renderedGhostLines = () =>
      ghostContent.evaluate((element) => (element.textContent ?? "").split("\n").length);
    await expect(ghostContent).toContainText("old line 0");
    await expect.poll(renderedGhostLines).toBeLessThan(100);
    await page.evaluate(() => {
      const editor = window.__WEAVIE_EDITOR__;
      const lineHeight = editor?.getOption(window.__WEAVIE_MONACO__.editor.EditorOption.lineHeight);
      if (editor !== undefined && lineHeight !== undefined) {
        editor.setScrollTop((5_000 - 10) * lineHeight);
      }
    });
    await expect(ghostContent).toContainText("old line 4999");
    await expect.poll(renderedGhostLines).toBeLessThan(100);
    await page.evaluate(() => window.__WEAVIE_EDITOR__?.revealLineInCenter(2_500));
    const paintedGhost = await ghostContent.elementHandle();
    if (paintedGhost === null) {
      throw new Error("large diff ghost not rendered");
    }

    // The recompute debounce matures at 120ms. A second edit at 130ms lands while the worker owns the old
    // version; it must stay responsive, supersede that result, and render only the final text.
    const typingPerformance = await page.evaluate(async (paintedGhost) => {
      const editor = window.__WEAVIE_EDITOR__;
      const model = editor?.getModel();
      if (editor === undefined || model === null || model === undefined) {
        throw new Error("editor model not ready");
      }
      editor.focus();
      const line = 2_500;
      const column = model.getLineMaxColumn(line);
      const metricsWindow = window as typeof window & {
        __WEAVIE_ZONE_TRANSACTIONS__?: Array<{
          added: number;
          diffAfter: boolean;
          diffBefore: boolean;
          diffAdds: number;
          heldAfter: boolean;
          heldBefore: boolean;
          removed: number;
        }>;
      };
      const zoneTransactions: NonNullable<typeof metricsWindow.__WEAVIE_ZONE_TRANSACTIONS__> = [];
      metricsWindow.__WEAVIE_ZONE_TRANSACTIONS__ = zoneTransactions;
      const changeViewZones = editor.changeViewZones.bind(editor);
      editor.changeViewZones = ((callback) =>
        changeViewZones((accessor) => {
          const transaction = {
            added: 0,
            diffAfter: false,
            diffBefore: document.querySelector(".weavie-inline-removed-line") !== null,
            diffAdds: 0,
            heldAfter: false,
            heldBefore: paintedGhost.isConnected,
            removed: 0,
          };
          callback({
            addZone: (zone) => {
              transaction.added++;
              if (zone.domNode.matches(".weavie-inline-removed-line")) {
                transaction.diffAdds++;
              }
              return accessor.addZone(zone);
            },
            removeZone: (id) => {
              transaction.removed++;
              accessor.removeZone(id);
            },
            layoutZone: (id) => accessor.layoutZone(id),
          });
          transaction.diffAfter = document.querySelector(".weavie-inline-removed-line") !== null;
          transaction.heldAfter = paintedGhost.isConnected;
          if (transaction.added > 0 || transaction.removed > 0) {
            zoneTransactions.push(transaction);
          }
        })) as typeof editor.changeViewZones;
      const reviewRev = window.__WEAVIE_REVIEW__?.rev ?? 0;
      const started = performance.now();
      editor.executeEdits("large-diff-typing", [
        {
          range: new window.__WEAVIE_MONACO__.Range(line, column, line, column),
          text: " typed",
        },
      ]);
      const editMs = performance.now() - started;
      const pendingPaint = {
        ghostConnected: paintedGhost.isConnected,
        zoneTransactions: zoneTransactions.length,
      };
      await new Promise((resolve) => setTimeout(resolve, 130));
      const secondStarted = performance.now();
      const secondColumn = model.getLineMaxColumn(line);
      editor.executeEdits("large-diff-typing-latest", [
        {
          range: new window.__WEAVIE_MONACO__.Range(line, secondColumn, line, secondColumn),
          text: "\nlatest line",
        },
      ]);
      const secondEditMs = performance.now() - secondStarted;
      await new Promise((resolve) => setTimeout(resolve, 20));
      return {
        editMs,
        secondEditMs,
        heartbeatMs: performance.now() - started,
        pendingPaint,
        reviewRev,
      };
    }, paintedGhost);
    await test.info().attach("large-diff-typing-performance.json", {
      body: Buffer.from(
        JSON.stringify({ budgetMs: TYPING_HEARTBEAT_BUDGET_MS, ...typingPerformance }),
      ),
      contentType: "application/json",
    });
    expect(
      typingPerformance.heartbeatMs,
      `5,000-line diff recomputation blocked the typing heartbeat: ${JSON.stringify(typingPerformance)}`,
    ).toBeLessThan(TYPING_HEARTBEAT_BUDGET_MS);
    expect(typingPerformance.pendingPaint).toEqual({
      ghostConnected: true,
      zoneTransactions: 0,
    });
    // The hunk coordinates are a version behind while the newer worker request is pending, so Keep/Revert say
    // so (dimmed) and the change-scoped shortcut is consumed without widening into Keep File.
    await expect(page.locator(KEEP_BTN)).toBeDisabled();
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect(page.locator(SCOPE)).toBeVisible();
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_REVIEW__?.rev ?? 0))
      .toBeGreaterThan(typingPerformance.reviewRev);
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(1);
    await expect
      .poll(() =>
        page.evaluate(
          () =>
            window.__WEAVIE_EDITOR__
              ?.getModel()
              ?.getAllDecorations()
              .find((decoration) => decoration.options.className === "weavie-inline-added")?.range
              .endLineNumber,
        ),
      )
      .toBe(5_001);
    await expect.poll(() => paintedGhost.evaluate((element) => element.isConnected)).toBe(false);
    const finalPaint = await page.evaluate(() => {
      const editor = window.__WEAVIE_EDITOR__;
      const metricsWindow = window as typeof window & {
        __WEAVIE_ZONE_TRANSACTIONS__?: Array<{
          added: number;
          diffAfter: boolean;
          diffBefore: boolean;
          diffAdds: number;
          heldAfter: boolean;
          heldBefore: boolean;
          removed: number;
        }>;
      };
      return {
        anchorVisible:
          editor
            ?.getVisibleRanges()
            .some((range) => range.startLineNumber <= 2_500 && range.endLineNumber >= 2_500) ??
          false,
        transactions: metricsWindow.__WEAVIE_ZONE_TRANSACTIONS__ ?? [],
      };
    });
    expect(finalPaint.anchorVisible).toBe(true);
    for (const transaction of finalPaint.transactions) {
      expect(transaction.removed > 0).toBe(transaction.diffAdds > 0);
      if (transaction.diffBefore && !transaction.diffAfter) {
        expect(transaction.diffAdds).toBeGreaterThan(0);
      }
    }
    const replacement = finalPaint.transactions.find(
      (transaction) => transaction.heldBefore && !transaction.heldAfter,
    );
    expect(replacement?.added).toBeGreaterThan(0);
    expect(replacement?.diffAdds).toBeGreaterThan(0);
    expect(replacement?.removed).toBeGreaterThan(0);
    await paintedGhost.dispose();
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getModel()?.getLineContent(2_500)))
      .toBe("new line 2499 typed");
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getModel()?.getLineContent(2_501)))
      .toBe("latest line");

    await focusFirstHunk(page);
    await expect(page.locator(KEEP_BTN)).toBeEnabled(); // the recompute landed; coordinates are current again
    await page.keyboard.press("ControlOrMeta+Enter");
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(0);
    await expect.poll(() => decorationCount(page, "weavie-inline-accepted")).toBe(1);
    await expect(page.locator(HIST_UNDO).first()).toBeEnabled();
    await page.keyboard.press("ControlOrMeta+Shift+Enter");
    await expect.poll(() => decorationCount(page, "weavie-inline-added")).toBe(1);

    await page.keyboard.press("ControlOrMeta+Backspace");
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getModel()?.getValue()))
      .toBe(original);
  });
});

test.describe("applied review — every file remains reviewable", () => {
  test.use({
    fakeScript: {
      steps: Array.from({ length: 100 }, (_, index) =>
        appliedEdit(`bulk-${index}.txt`, `change ${index}\n`),
      ).flat(),
    },
  });

  test("does not truncate a large review set", async ({ page }) => {
    await openFile(page, "README.md");

    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_REVIEW__?.files.length ?? 0))
      .toBe(100);
    await expect(page.locator(".weavie-inline-stack-sub")).toContainText("100 files");

    await openFile(page, "bulk-0.txt");
    await page.locator(".weavie-inline-scope-btn").click();
    await page.locator(".weavie-inline-scope-item", { hasText: "All files" }).click();
    await page.locator(".weavie-inline-accept").click();

    await expect
      .poll(() => page.evaluate(() => window.__WEAVIE_REVIEW__?.files.length ?? -1))
      .toBe(0);
    await expect(page.locator(TOOLBAR)).toHaveCount(0);
  });
});
