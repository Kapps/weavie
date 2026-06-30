import { readFileSync } from "node:fs";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
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
const HIST_UNDO = ".weavie-inline-hist"; // the toolbar's ↶ Undo (first) / ↷ Redo history buttons
const TOOLBAR = ".weavie-inline-toolbar";

// Land the caret on the first hunk deterministically (next-change from the top of the file), so a per-hunk
// keep/revert acts on a known hunk regardless of where the file opened.
async function focusFirstHunk(page: import("@playwright/test").Page): Promise<void> {
  await expect(page.locator(SCOPE)).toBeVisible({ timeout: 15_000 });
  await page.keyboard.press("ControlOrMeta+ArrowDown");
}

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
    // Give any (buggy) autosave of an inserted newline time to reach disk, then assert nothing changed.
    await page.waitForTimeout(1500);
    expect(read(weavie.workspace, "hello.ts")).toBe(before);
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
    await page.keyboard.press("ControlOrMeta+ArrowDown");
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
