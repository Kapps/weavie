import { chmodSync, existsSync, readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { allowAutomaticInference, clickIntoEditor, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { appliedEdit } from "../harness/review";
import { fakeClaudeProgram } from "../harness/test-programs";

// VIDEO TOUR (not a committed regression spec — the C# HostCoreLearnTests pin this at the TestHost seam):
// the full learn-from-corrections journey over the REAL stack — fake-claude turns (UserPromptSubmit with a
// prompt + PreToolUse/edit/PostToolUse over the hook-bridge pipe), user corrections captured AT the user's
// action (a review-UI hunk revert, then Monaco hand-edits over the agent's lines), the per-workspace ring
// filling to the threshold, the "Learn from your corrections?" card, Yes → ONE isolated `claude --print`
// inference query while the read-only `about:corrections` tab spins, the proposed rules rendered into that
// tab, the ring consumed, and a re-run inside the 24-hour interval reopening the kept analysis instead of
// asking again.
//
// The model is stubbed at the process seam like every other journey here: the claude.path wrapper is swapped
// for one that answers `--print` with a canned CorrectionLessonsOutput envelope (and records the prompt it was
// given), so no real model ever runs.

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
const LONG_AGENT = `${Array.from({ length: 160 }, (_, i) => `// step ${i + 1}`).join("\n")}\n`;

const SIG_2 = ".sig-boundary-2";
const SIG_3 = ".sig-boundary-3";
const SIG_4 = ".sig-boundary-4";

// Turn prompts — the boundary hook carries a `prompt` field, so the analysis sees what produced each edit.
const PROMPT_1 = "Make the greeting friendlier";
const PROMPT_2 = "Write up the deploy steps in notes.txt";
const PROMPT_3 = "Rewrite the README intro";
const PROMPT_4 = "Number the steps in long.ts";

// What the stubbed model answers with — the exact CorrectionLessonsOutput shape the query decodes strictly.
const LESSONS = {
  rules: [
    {
      rule: "Keep user-facing copy as written: don't reword greetings like `Hello, <name>!` unless asked.",
      evidence:
        "Correction 1 (hello.ts): the reverted hunk restored `Hello, ${name}!` over `Hi there, ${name}!`.",
    },
    {
      rule: "Never replace an existing README intro wholesale — extend the section instead.",
      evidence:
        "Correction 3 (README.md): the rewritten intro was hand-edited back to the original wording.",
    },
    {
      rule: "Say what a document is for in its first line; a bare list of steps loses the reader.",
      evidence: "Correction 2 (notes.txt): the deploy list gained a `(reviewed)` heading edit.",
    },
  ],
  summary:
    "Across three corrections the user consistently restored their own wording over rewrites of existing text.",
};

test.use({
  // The offer toast stays up so the tour can take the real in-app path to enabling inference.
  dismissInferenceOffer: false,
  fakeScript: {
    steps: [
      // Claude configures Weavie over MCP so the nudge arrives after three corrections instead of ten.
      { op: "mcp", tool: "setSetting", args: { key: "corrections.learnThreshold", value: 3 } },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_1 } },
      ...appliedEdit("hello.ts", HELLO_AGENT),
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_2}` },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_2 } },
      ...appliedEdit("notes.txt", NOTES_AGENT),
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_3}` },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_3 } },
      ...appliedEdit("README.md", README_AGENT),
      // The fourth turn runs AFTER the analysis, so the 24-hour refusal is met with a NON-empty ring. Its
      // one correction also clears the (now lowered) nudge threshold, leaving the cooldown as the only thing
      // that can keep the card away.
      { op: "waitFile", path: `{{WORKSPACE}}/${SIG_4}` },
      { op: "mcp", tool: "setSetting", args: { key: "corrections.learnThreshold", value: 1 } },
      { op: "hook", request: { hook_event_name: "UserPromptSubmit", prompt: PROMPT_4 } },
      ...appliedEdit("long.ts", LONG_AGENT),
    ],
  },
});

// The workspace's correction ring on disk (~/.weavie/workspaces/<id>/corrections.jsonl in the isolated HOME).
function ringLines(home: string): number {
  return workspaceFile(home, "corrections.jsonl")
    .split("\n")
    .filter((l) => l.trim().length > 0).length;
}

function workspaceFile(home: string, name: string): string {
  const root = join(home, ".weavie", "workspaces");
  if (!existsSync(root)) {
    return "";
  }
  for (const id of readdirSync(root)) {
    const file = join(root, id, name);
    if (existsSync(file)) {
      return readFileSync(file, "utf8");
    }
  }
  return "";
}

// Swap the claude.path wrapper for one that answers the ad-hoc inference call (`claude --print`) with a canned
// structured envelope, dumping the prompt it was handed to `<home>/inference-prompt.txt` first. Everything else
// (the claude PANE) still execs the real fake-claude, so the already-running session is untouched. The envelope
// is written to its own file and `cat`ed, so the answer's own backticks/`${}` never reach the shell.
function stubInference(home: string, envelope: unknown): void {
  const answer = join(home, "inference-answer.json");
  writeFileSync(answer, JSON.stringify(envelope));
  const wrapper = join(home, "fake-claude.sh");
  const real = [fakeClaudeProgram.command, ...fakeClaudeProgram.args]
    .map((part) => JSON.stringify(part))
    .join(" ");
  writeFileSync(
    wrapper,
    "#!/bin/sh\n" +
      'case " $* " in\n' +
      '  *" --print "*)\n' +
      `    cat > ${JSON.stringify(join(home, "inference-prompt.txt"))}\n` +
      // A visible pause: the tab's spinner is on camera, and the in-flight second run is refused inside it.
      "    sleep 9\n" +
      `    exec cat ${JSON.stringify(answer)}\n` +
      "    ;;\n" +
      "esac\n" +
      `exec ${real} "$@"\n`,
  );
  chmodSync(wrapper, 0o755);
}

test("corrections accumulate, the card offers the analysis, and the rules open in a tab", async ({
  page,
  weavie,
}) => {
  test.setTimeout(300_000);
  const card = page.locator(".suggestion", { hasText: "Learn from your corrections?" });
  const read = (rel: string) => readFileSync(join(weavie.workspace, rel), "utf8");
  const learnTab = page.locator(".editor-tab", { hasText: "What Your Corrections Suggest" });
  const source = page.locator(".editor-source .wv-source");

  // The repository's existing rules — the analysis carries them so it never re-proposes what's already there.
  writeFileSync(
    join(weavie.workspace, "AGENTS.md"),
    "# Sample project\n\n- Never add fallbacks.\n- Keep comments to one line.\n",
  );

  // ── An empty ring refuses loudly, and never reaches the model ────────────────────────────────────────
  await runCommand(page, "Learn From My Corrections");
  await expect(page.locator(".toast", { hasText: "No corrections recorded yet" })).toBeVisible();
  await hold(page, 2500);

  // ── The user turns ad-hoc inference on, in-app, from the offer toast ─────────────────────────────────
  await allowAutomaticInference(page);
  await hold(page, 1500);

  // ── Correction 1: revert a hunk in the inline review UI (recorded AT the revert) ─────────────────────
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-inline-added")).toHaveCount(2, { timeout: 20_000 });
  await hold(page, 1800); // show the agent's pending diff
  await page.locator(".weavie-inline-pending-revert").first().hover();
  await hold(page, 1200);
  await page.locator(".weavie-inline-pending-revert").first().click();
  await expect.poll(() => read("hello.ts")).toContain("Hello, ${name}"); // baseline restored on disk
  await hold(page, 1500);

  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(1);
  await expect(page.locator(".suggestion")).toHaveCount(0); // 1 < 3
  writeFileSync(join(weavie.workspace, SIG_2), ""); // let fake-claude run its next turn (notes.txt)

  // ── Correction 2: hand-edit the agent's first line in the editor (autosave records the correction) ───
  await openFile(page, "notes.txt");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 20_000 });
  await hold(page, 1200);
  await clickIntoEditor(page);
  await page.keyboard.press("ControlOrMeta+Home");
  await page.keyboard.press("End");
  await page.keyboard.type(" (reviewed)", { delay: 40 });
  await expect.poll(() => read("notes.txt"), { timeout: 20_000 }).toContain("(reviewed)");
  await hold(page, 1200);
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(2);
  await expect(page.locator(".suggestion")).toHaveCount(0); // still below the threshold of 3
  writeFileSync(join(weavie.workspace, SIG_3), ""); // next turn (README.md)

  // ── Correction 3: hand-edit the agent's rewritten README line ────────────────────────────────────────
  await openFile(page, "README.md");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 20_000 });
  await hold(page, 1200);
  await page.locator(".view-line", { hasText: "Weavie rewrote" }).click();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await page.keyboard.type("Keep the original intro, please.", { delay: 40 });
  await expect.poll(() => read("README.md"), { timeout: 20_000 }).toContain("Keep the original");
  await hold(page, 1200);
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(3);

  // ── The nudge appears at the threshold ───────────────────────────────────────────────────────────────
  await expect(card).toBeVisible({ timeout: 20_000 });
  await expect(card).toContainText("mine those reverts and edits");
  await hold(page, 2500); // let the card sit on camera

  // The model is stubbed at the process seam before the card is taken up — no real model ever runs.
  stubInference(weavie.home, {
    is_error: false,
    session_id: "fake-lessons",
    structured_output: LESSONS,
  });

  await card.locator(".suggestion-action.primary", { hasText: "Yes" }).hover();
  await hold(page, 1200);
  await card.locator(".suggestion-action.primary", { hasText: "Yes" }).click();

  // ── The tab opens SPINNING and the command returns immediately ───────────────────────────────────────
  await expect(page.locator(".toast", { hasText: "Analyzing 3 correction(s)" })).toBeVisible();
  await expect(learnTab).toBeVisible({ timeout: 20_000 });
  await expect(learnTab.locator("svg.editor-tab-icon")).toBeVisible(); // the corrections (mortarboard) icon
  await expect(source.locator(".wv-status")).toContainText("Loading…");
  await hold(page, 2000);

  // A second run while that one is still in flight is refused — one analysis at a time.
  await runCommand(page, "Learn From My Corrections");
  await expect(
    page.locator(".toast", { hasText: "Your corrections are already being analyzed." }),
  ).toBeVisible();
  await hold(page, 2000);

  // ── … then resolves to the model's proposed rules ────────────────────────────────────────────────────
  await expect(source.locator("ol li").first()).toBeVisible({ timeout: 60_000 });
  await expect(source).toContainText(LESSONS.summary);
  await expect(source.locator("ol li")).toHaveCount(3);
  await expect(source.locator(".wv-learn-note").first()).toContainText("3 corrections · sonnet");
  await expect(source.locator(".wv-learn-evidence").first()).toContainText(
    "Correction 1 (hello.ts)",
  );
  // The model's own words reach an innerHTML sink, so its markup is TEXT, not structure.
  await expect(source.locator("ol li").first()).toContainText("`Hello, <name>!`");
  await expect(source.locator("ol li em")).toHaveCount(0);
  await hold(page, 3500);
  // Acting on the rules is one copy, not a retype: the paste-ready block carries every rule as a bullet.
  await source.locator("pre").scrollIntoViewIfNeeded();
  await expect(source.locator("pre")).toContainText(`- ${LESSONS.rules[0]?.rule}`);
  await expect(source.locator("pre")).toContainText(`- ${LESSONS.rules[2]?.rule}`);
  await hold(page, 3500);

  // ── ONE isolated query, carrying the corpus and the repository's existing instructions ───────────────
  const prompt = readFileSync(join(weavie.home, "inference-prompt.txt"), "utf8");
  expect(prompt).toContain("Never add fallbacks"); // the repo's AGENTS.md rode along
  expect(prompt).toContain(PROMPT_1);
  expect(prompt).toContain(PROMPT_2);
  expect(prompt).toContain(PROMPT_3);
  expect(prompt).toContain("-  return `Hi there, ${name}!`;"); // what the agent wrote …
  expect(prompt).toContain("+  return `Hello, ${name}!`;"); // … and what the user changed it to
  expect(prompt).toContain("+Keep the original intro, please.");

  // ── The analyzed ring was consumed and the card withdrew; the day is stamped ─────────────────────────
  await expect.poll(() => ringLines(weavie.home)).toBe(0);
  await expect(card).toHaveCount(0);
  expect(workspaceFile(weavie.home, "learn.json")).toContain("lastRunUtc");

  // ── A fresh correction after the analysis: the ring refills, but today's allowance is spent ──────────
  writeFileSync(join(weavie.workspace, SIG_4), ""); // fake-claude's fourth turn (long.ts)
  await openFile(page, "long.ts");
  await expect(page.locator(".weavie-inline-added").first()).toBeVisible({ timeout: 20_000 });
  await page.locator(".view-line", { hasText: "// step 1" }).first().click();
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await page.keyboard.type("// 1. do the first step", { delay: 40 });
  await expect.poll(() => read("long.ts"), { timeout: 20_000 }).toContain("1. do the first step");
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(1);
  await hold(page, 1200);

  // The nudge threshold is now 1 and the ring holds 1 — yet no card, because the day's analysis is spent.
  expect(weavie.fakeLog()).toContain("Set corrections.learnThreshold to 1");
  await expect(page.locator(".suggestion")).toHaveCount(0);

  // ── Close the analysis: its ring is already spent, so the kept result is the only copy ───────────────
  await learnTab.locator(".editor-tab-close").click();
  await expect(learnTab).toHaveCount(0);
  await hold(page, 1500);

  // ── Running it again inside the interval reopens that analysis instead of spending a second query ────
  await runCommand(page, "Learn From My Corrections");
  const held = page.locator(".toast", { hasText: "once every 24 hours" });
  await expect(held).toBeVisible();
  await expect(held).toContainText("next analysis is available in 24 hours");
  await expect(held).toContainText("Reopened your most recent analysis.");
  await expect(learnTab).toBeVisible();
  await expect(source.locator("ol li")).toHaveCount(3);
  await expect(source).toContainText(LESSONS.summary);
  await hold(page, 4000);

  // Reopening cost nothing: the new correction still waits for tomorrow, and the model was asked ONCE.
  expect(ringLines(weavie.home)).toBe(1);
  expect(readFileSync(join(weavie.home, "inference-prompt.txt"), "utf8")).toBe(prompt);
});

test("a failed analysis shows the reason in the tab and spends neither the ring nor the day", async ({
  page,
  weavie,
}) => {
  test.setTimeout(300_000);
  const read = (rel: string) => readFileSync(join(weavie.workspace, rel), "utf8");
  const source = page.locator(".editor-source .wv-source");

  // Inference stays OFF (the offer toast is never accepted) — the most likely real-world failure.
  await page.locator(".toast", { hasText: "Let Weavie use automatic inference" }).waitFor();
  await hold(page, 1500);

  // One correction: revert the agent's hunk in the review UI.
  await openFile(page, "hello.ts");
  await expect(page.locator(".weavie-inline-added")).toHaveCount(2, { timeout: 20_000 });
  await hold(page, 1500);
  await page.locator(".weavie-inline-pending-revert").first().click();
  await expect.poll(() => read("hello.ts")).toContain("Hello, ${name}");
  await expect.poll(() => ringLines(weavie.home), { timeout: 20_000 }).toBe(1);
  await hold(page, 1200);

  // The command still opens the tab and returns — the failure lands where the user is looking.
  await runCommand(page, "Learn From My Corrections");
  await expect(page.locator(".toast", { hasText: "Analyzing 1 correction(s)" })).toBeVisible();
  await expect(source.locator(".wv-status.wv-error")).toContainText(
    "Couldn't analyze your corrections: Ad-hoc inference is disabled.",
    { timeout: 30_000 },
  );
  await hold(page, 4000);

  // Neither the corpus nor the day was spent: the ring still holds the correction and a re-run is allowed.
  expect(ringLines(weavie.home)).toBe(1);
  await runCommand(page, "Learn From My Corrections");
  await expect(page.locator(".toast", { hasText: "Analyzing 1 correction(s)" })).toBeVisible();
  await expect(page.locator(".toast", { hasText: "once every 24 hours" })).toHaveCount(0);
  await hold(page, 3000);
  expect(ringLines(weavie.home)).toBe(1);
});
