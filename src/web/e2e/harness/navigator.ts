import { type Page, expect } from "@playwright/test";

// The inline-diff navigator's file walk, shared by every PR/review spec. Each step waits for the navigator's
// file label to actually advance — the state event — instead of a fixed delay, which was the suite's #1 flake
// source (see docs/specs/integration-testing-strategy.md, principle 4).

const STACK_NAME = ".weavie-inline-stack-name";

/** The change navigator's chord for an arrow — ctrl+$mod: plain Ctrl on Win/Linux, ⌃⌘ on Mac. */
export function navChord(arrow: "ArrowUp" | "ArrowDown" | "ArrowLeft" | "ArrowRight"): string {
  return process.platform === "darwin" ? `Control+Meta+${arrow}` : `Control+${arrow}`;
}

// A real changed-file label carries an extension; during a session-switch rebind the label transiently reads
// the parked cue ("Review changes") before it binds to the incoming diff, so we only ever trust filenames.
const isFileName = (s: string): boolean => /\.\w+$/.test(s);

// The navigator's current stack label, trimmed; "" before the first diff renders.
async function currentFile(page: Page): Promise<string> {
  return (await page.locator(STACK_NAME).textContent())?.trim() ?? "";
}

// Wait for the label to settle on a real filename (past any transient parked cue) and return it.
async function settledFile(page: Page): Promise<string> {
  await expect.poll(() => currentFile(page)).toMatch(/\.\w+$/);
  return currentFile(page);
}

// Click into the inline-diff/editor pane and confirm focus landed there. The change-nav chords are guarded
// `!terminalFocused`, so they only reach the navigator when the editor holds focus — but a session switch
// focuses Claude's terminal, and a trailing focus-pane from a switch storm can steal it back mid-walk, so the
// chord silently falls through to xterm (the Windows PR-switch flake). Re-click until the focused pane is the
// editor; fail loudly if it can't be held.
export async function focusEditor(page: Page): Promise<void> {
  const focusedPane = (): Promise<string | null> =>
    page.evaluate(
      () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
    );
  for (let attempt = 0; attempt < 10; attempt++) {
    await page.locator(".monaco-editor").first().click();
    try {
      await expect.poll(focusedPane, { timeout: 1_000 }).toBe("editor");
      return;
    } catch {
      // A trailing focus-pane stole focus back to the terminal; click again once it settles.
    }
  }
  throw new Error("editor never held focus — a focus-pane kept stealing it");
}

// Step the navigator to the next changed file (Ctrl/Cmd+→) and resolve once the label has advanced to a
// different filename. Returns the file now in view. Editor focus is re-asserted before every press (the chord
// is `!terminalFocused`-guarded), so a focus-pane stealing focus mid-walk can't silently swallow the chord.
// The chord is re-sent when the label hasn't moved: a press that lands while a switch's message train is
// still applying can be consumed by a transient state (like a user, press again once it settles); a
// navigator that's genuinely dead still fails loudly after the attempts run out.
async function stepToNextFile(page: Page, from: string): Promise<string> {
  for (let attempt = 0; attempt < 8; attempt++) {
    await focusEditor(page);
    await page.keyboard.press(navChord("ArrowRight"));
    try {
      await expect
        .poll(
          async () => {
            const name = await currentFile(page);
            return isFileName(name) && name !== from;
          },
          { timeout: 2_000 },
        )
        .toBe(true);
      return currentFile(page);
    } catch {
      // Label unchanged — the press was eaten mid-churn; re-arm it.
    }
  }
  throw new Error(`navigator never advanced past ${from}`);
}

// The set of changed files the navigator cycles through, gathered by walking → until it loops back to the
// first. Event-based, so it neither misses a file under load nor wastes time waiting on a fixed delay.
export async function collectChangedFiles(page: Page): Promise<Set<string>> {
  await focusEditor(page);
  const first = await settledFile(page);
  const seen = new Set<string>([first]);
  let current = first;
  for (let i = 0; i < 12; i++) {
    current = await stepToNextFile(page, current);
    if (current === first) {
      break; // cycled back — every changed file has been seen
    }
    seen.add(current);
  }
  return seen;
}

// Wait until the navigator has bound the INCOMING PR's diff after a session switch — its stack label shows
// one of that PR's files (the pr-changes rebind auto-opens the first). The toolbar alone is not a settle
// signal: right after the switch the OUTGOING session's toolbar is still on screen until the incoming
// pr-changes lands, and walking then collects the wrong PR's files.
export async function awaitNavigatorOn(page: Page, files: string[]): Promise<void> {
  await expect
    .poll(async () => files.includes(await currentFile(page)), { timeout: 15_000 })
    .toBe(true);
}

// Walk the navigator forward until `target` is in view, then assert it arrived. Replaces the per-step
// fixed-delay loops the PR specs used to reach a specific changed file.
export async function walkToChangedFile(page: Page, target: string): Promise<void> {
  await focusEditor(page);
  for (let i = 0; i < 6; i++) {
    const current = await settledFile(page);
    if (current === target) {
      break;
    }
    await stepToNextFile(page, current);
  }
  await expect(page.locator(STACK_NAME)).toHaveText(target);
}
