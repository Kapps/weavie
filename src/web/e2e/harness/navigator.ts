import { type Page, expect } from "@playwright/test";

// The inline-diff navigator's file walk, shared by every PR/review spec. Each step waits for the navigator's
// file label to actually advance — the state event — instead of a fixed delay, which was the suite's #1 flake
// source (see docs/specs/integration-testing-strategy.md, principle 4).

const STACK_NAME = ".weavie-inline-stack-name";

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

// Step the navigator to the next changed file (Ctrl/Cmd+→) and resolve once the label has advanced to a
// different filename. Returns the file now in view. The editor must already hold focus so the chord lands.
async function stepToNextFile(page: Page, from: string): Promise<string> {
  await page.keyboard.press("ControlOrMeta+ArrowRight");
  await expect
    .poll(async () => {
      const name = await currentFile(page);
      return isFileName(name) && name !== from;
    })
    .toBe(true);
  return currentFile(page);
}

// The set of changed files the navigator cycles through, gathered by walking → until it loops back to the
// first. Event-based, so it neither misses a file under load nor wastes time waiting on a fixed delay.
export async function collectChangedFiles(page: Page): Promise<Set<string>> {
  await page.locator(".monaco-editor").first().click();
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
  await page.locator(".monaco-editor").first().click();
  for (let i = 0; i < 6; i++) {
    const current = await settledFile(page);
    if (current === target) {
      break;
    }
    await stepToNextFile(page, current);
  }
  await expect(page.locator(STACK_NAME)).toHaveText(target);
}
