import { readFileSync } from "node:fs";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

// Regression: pressing Ctrl+Enter to Keep an agent hunk used to FAIL (with a "<file> changed — re-open to
// review" toast) whenever the user had first hand-edited a DIFFERENT region of the same file — an edit that
// shifted the agent hunk's line number. The web diffs the live model, so it sent the shifted (disk-space)
// position; Core mis-mapped it through _current (which omits the user's non-agent edit) and its guard aborted.
// The fix guards KeepHunk against disk directly. This drives the whole stack: fake → hook bridge → tracker →
// turn-diff → inline toolbar → manual type + autosave (fs-write → RecordHandEdit) → keep → Core.

// The agent rewrites the greeting line (one hunk on line 2 of the seeded hello.ts).
const AGENT_EDIT =
  "export function greet(name: string): string {\n" +
  "  return `Hi there, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.log(message);\n";

const ADDED = ".weavie-inline-added"; // one decoration per BRIGHT (pending) agent-authored changed line
const USER = ".weavie-inline-user"; // one per line the user typed themselves (faint), diffed vs the agent copy
const ACCEPTED = ".weavie-inline-accepted"; // one per FADED (kept-but-uncommitted) line
const STALE_TOAST = ".toast-warn"; // the "changed — re-open to review" warn toast a failed keep would raise

const read = (workspace: string, rel: string): string => readFileSync(join(workspace, rel), "utf8");

// Land the caret on the agent's hunk (the "Hi there" line) via the real editor, wherever the hand edit pushed it.
async function caretOnAgentHunk(page: Page): Promise<void> {
  await page.evaluate(() => {
    const editor = (
      window as Window & {
        __WEAVIE_EDITOR__?: {
          getModel(): { getLinesContent(): string[] } | null;
          setPosition(p: { lineNumber: number; column: number }): void;
          focus(): void;
        };
      }
    ).__WEAVIE_EDITOR__;
    const lines = editor?.getModel()?.getLinesContent() ?? [];
    const idx = lines.findIndex((l) => l.includes("Hi there"));
    editor?.setPosition({ lineNumber: idx + 1, column: 1 });
    editor?.focus();
  });
}

test.describe("keep after a non-agent hand edit", () => {
  test.use({ fakeScript: { steps: [...appliedEdit("hello.ts", AGENT_EDIT)] } });

  test("Ctrl+Enter keeps the agent hunk after the user edits an unrelated region (no stale toast)", async ({
    page,
    weavie,
  }) => {
    await openFile(page, "hello.ts");
    await expect(page.locator(ADDED)).toHaveCount(1); // the agent's one bright pending hunk (the greeting)
    await expect(page.locator(ACCEPTED)).toHaveCount(0);

    // The user prepends an unrelated line at the very top — a region the agent never authored — shifting the
    // agent hunk down. Autosave flushes it to disk, where the host records it as a hand edit: disk now diverges
    // from the tracker's agent-only _current, which is exactly the state that used to break the keep.
    await page.locator(".monaco-editor .view-lines").first().click();
    await page.keyboard.press("ControlOrMeta+Home");
    await page.keyboard.type("const myOwnNote = 1;\n");

    // Wait for the edit to reach disk (autosave), so the host has recorded the hand edit before we keep.
    await expect.poll(() => read(weavie.workspace, "hello.ts")).toContain("const myOwnNote");
    // The typed text renders as the user's own (faint) band, distinct from the agent's bright hunk — proof the
    // two regions are tracked apart and the agent hunk shifted down.
    await expect(page.locator(USER).first()).toBeVisible();
    await expect(page.locator(ADDED)).toHaveCount(1); // agent hunk still bright, now on a lower line

    // Keep the agent hunk: caret on it, Ctrl+Enter. Before the fix this aborted with a stale toast; after it, the
    // hunk leaves the bright pending band and becomes accepted (faded).
    await caretOnAgentHunk(page);
    await page.keyboard.press("ControlOrMeta+Enter");

    await expect(page.locator(ADDED)).toHaveCount(0); // the agent hunk left the pending band — kept
    await expect(page.locator(ACCEPTED)).toHaveCount(1); // it's now the faded accepted band, not gone
    await expect(page.locator(STALE_TOAST)).toHaveCount(0); // and NO "changed — re-open to review" toast

    // The user's own line is untouched by the keep, still on disk alongside the kept agent change.
    expect(read(weavie.workspace, "hello.ts")).toContain("const myOwnNote");
    expect(read(weavie.workspace, "hello.ts")).toContain("Hi there");
  });
});
