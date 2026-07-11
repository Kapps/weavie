import { existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";

// VIDEO TOUR (not a committed regression spec — the C# HostCoreLearnTests pin this at the TestHost seam):
// the full learn-from-corrections journey over the REAL stack — fake-claude turns (UserPromptSubmit with a
// prompt + PreToolUse/edit/PostToolUse over the hook-bridge pipe), three user corrections (a review-UI hunk
// revert, a Monaco hand-edit, an out-of-band disk edit), the per-workspace ring filling to the default
// threshold (3), the "Teach Claude from your corrections?" card, Yes → the analysis prompt bracket-pasted
// into the claude pane WITHOUT submitting, the ring consumed, and the empty-ring palette failure.

const hold = (page: import("@playwright/test").Page, ms: number) => page.waitForTimeout(ms);

// Agent output per turn (what fake-claude writes); the user corrects each one differently.
const HELLO_AGENT =
  "export function greet(name: string): string {\n" +
  "  return `Hi there, ${name}!`;\n" +
  "}\n\n" +
  'const message = greet("weavie");\n' +
  "console.warn(message);\n";
const NOTES_AGENT = "Deploy steps:\n1. build\n2. ship\n";
const README_AGENT = "# Sample project\n\nWeavie rewrote this intro entirely.\n";
const README_USER = "# Sample project\n\nKeep the original intro, please.\n";

const SIG_1 = ".sig-boundary-2";
const SIG_2 = ".sig-boundary-3";
const SIG_3 = ".sig-boundary-4";

// Turn prompts — the boundary hook carries a `prompt` field, so the analysis attributes each correction.
const PROMPT_1 = "Make the greeting friendlier";
const PROMPT_2 = "Write up the deploy steps in notes.txt";
const PROMPT_3 = "Rewrite the README intro";

test.use({
  fakeScript: {
    steps: [
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_1 } },
      ...appliedEdit("hello.ts", HELLO_AGENT),
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_1}` },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_2 } },
      ...appliedEdit("notes.txt", NOTES_AGENT),
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_2}` },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_3 } },
      ...appliedEdit("README.md", README_AGENT),
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_3}` },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: "Now add some tests" } },
      // Script ends here: fake-claude falls into its stdin-drain loop, so the bracketed paste is consumed
      // (and echoed by the PTY line discipline) rather than backing up the tty input buffer.
    ],
  },
});

// The workspace's correction ring on disk (~/.weavie/workspaces/<id>/corrections.jsonl in the isolated HOME).
function ringLines(home: string): number {
  const root = join(home, ".weavie", "workspaces");
  if (!existsSync(root)) {
    return 0;
  }
  for (const id of readdirSync(root)) {
    const file = join(root, id, "corrections.jsonl");
    if (existsSync(file)) {
      return readFileSync(file, "utf8")
        .split("\n")
        .filter((l) => l.trim().length > 0).length;
    }
  }
  return 0;
}

// Everything the claude pane's xterm holds (scrollback included), wrap-joined so a hard-wrapped long line
// still matches as one substring. Search strings must not span a hard newline.
function claudeText(page: import("@playwright/test").Page): Promise<string> {
  return page.evaluate(() => {
    const entry = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    );
    if (!entry) {
      return "";
    }
    const buf = entry[1].buffer.active;
    let out = "";
    for (let i = 0; i < buf.length; i++) {
      out += buf.getLine(i)?.translateToString(true) ?? "";
    }
    return out;
  });
}

// Scroll the claude pane to the first buffer line containing `needle` ("" = back to the bottom).
const scrollClaude = (page: import("@playwright/test").Page, needle: string) =>
  page.evaluate((n) => {
    const entry = Object.entries(window.__WEAVIE_TERMINALS__ ?? {}).find(([key]) =>
      key.endsWith(":claude"),
    );
    if (!entry) {
      return;
    }
    const term = entry[1];
    if (n === "") {
      term.scrollToBottom();
      return;
    }
    const buf = term.buffer.active;
    for (let i = 0; i < buf.length; i++) {
      if ((buf.getLine(i)?.translateToString(true) ?? "").includes(n)) {
        term.scrollToLine(i);
        return;
      }
    }
  }, needle);

test("three corrections fill the ring, the card offers /learn, Yes prefills the analysis unsubmitted", async ({
  page,
  weavie,
}) => {
  test.setTimeout(240_000);
  const card = page.locator(".suggestion", { hasText: "Teach Claude from your corrections?" });
  const read = (rel: string) => readFileSync(join(weavie.workspace, rel), "utf8");

  // ── Correction 1: revert a hunk in the inline review UI ──────────────────────────────────────────────
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-inline-added")).toHaveCount(2, { timeout: 20_000 });
  await hold(page, 1800); // show the agent's pending diff
  await page.locator(".weavie-inline-pending-revert").first().hover();
  await hold(page, 1200);
  await page.locator(".weavie-inline-pending-revert").first().click();
  await expect.poll(() => read("hello.ts")).toContain("Hello, ${name}"); // baseline restored on disk
  await expect(page.locator(".weavie-inline-added")).toHaveCount(1);
  await hold(page, 1500);

  // Nothing recorded until the next turn boundary; no card anywhere.
  expect(ringLines(weavie.home)).toBe(0);
  await expect(page.locator(".suggestion")).toHaveCount(0);

  // Next prompt boundary → the revert is drained into the ring.
  writeFileSync(join(weavie.workspace, SIG_1), "");
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(1);

  // ── Correction 2: hand-edit the agent's output in the editor ─────────────────────────────────────────
  await openFile(page, "notes.txt");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 20_000 });
  await hold(page, 1200);
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+End");
  await page.keyboard.type("3. run the smoke tests first", { delay: 40 });
  await expect.poll(() => read("notes.txt"), { timeout: 20_000 }).toContain("smoke tests"); // autosaved
  await hold(page, 1200);

  writeFileSync(join(weavie.workspace, SIG_2), "");
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(2);
  await expect(page.locator(".suggestion")).toHaveCount(0); // still below the threshold of 3

  // ── Correction 3: hand-edit on disk, outside the app entirely ────────────────────────────────────────
  writeFileSync(join(weavie.workspace, "README.md"), README_USER);
  writeFileSync(join(weavie.workspace, SIG_3), "");
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(3);

  // ── The nudge appears at the default threshold (3) ───────────────────────────────────────────────────
  await expect(card).toBeVisible({ timeout: 20_000 });
  await expect(card).toContainText("mine those reverts and edits");
  await hold(page, 2500); // let the card sit on camera
  await card.locator(".suggestion-action.primary", { hasText: "Yes" }).hover();
  await hold(page, 1200);

  // ── Yes → the analysis prompt lands in the claude pane as a bracketed paste, NOT submitted ───────────
  await card.locator(".suggestion-action.primary", { hasText: "Yes" }).click();
  // Wait for the paste's LAST line to render before snapshotting — the echo streams over the bridge.
  await expect
    .poll(() => claudeText(page), { timeout: 20_000 })
    .toContain("+Keep the original intro, please.");

  const text = await claudeText(page);
  expect(text).toContain("Weavie recorded corrections the user made to your output");
  expect(text).toContain("## Correction 1");
  expect(text).toContain("## Correction 3");
  expect(text).toContain(`Prompt: ${PROMPT_1}`);
  expect(text).toContain(`Prompt: ${PROMPT_2}`);
  expect(text).toContain(`Prompt: ${PROMPT_3}`);
  // The deltas read -what-the-agent-wrote / +what-the-user-changed-it-to.
  expect(text).toContain("-  return `Hi there, ${name}!`;");
  expect(text).toContain("+  return `Hello, ${name}!`;");
  expect(text).toContain("+3. run the smoke tests first");
  expect(text).toContain("-Weavie rewrote this intro entirely.");
  expect(text).toContain("+Keep the original intro, please.");
  // The paste is the LAST thing in the pane — nothing (no submit echo, no response) follows its final
  // diff line. The byte-level "no trailing CR" proof lives in HostCoreLearnTests; this is the UI view.
  console.log(`[buffer-head] ${JSON.stringify(text.slice(0, 160))}`);
  expect(text.trimEnd()).toMatch(/\+Keep the original intro, please\.\s*```$/);

  // Show the analysis on camera: the header, then the first correction, then back to the tail.
  await scrollClaude(page, "Weavie recorded corrections");
  await hold(page, 2600);
  await scrollClaude(page, "## Correction 1");
  await hold(page, 2600);
  await scrollClaude(page, "");
  await hold(page, 2000);

  // The ring was consumed and the card withdrew.
  await expect.poll(() => ringLines(weavie.home)).toBe(0);
  await expect(card).toHaveCount(0);

  // ── Empty ring: the palette command fails loudly, not a quiet no-op ──────────────────────────────────
  await runCommand(page, "Learn From My Corrections");
  const toast = page.locator(".toast.toast-warn", { hasText: "No corrections recorded yet" });
  await expect(toast).toBeVisible({ timeout: 15_000 });
  await hold(page, 2500);
});
