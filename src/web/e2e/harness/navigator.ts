import { type Page, expect } from "@playwright/test";

// The inline-diff navigator's file walk, shared by every PR/review spec. Each step waits for the navigator's
// file label to actually advance — the state event — instead of a fixed delay, which was the suite's #1 flake
// source (see docs/specs/integration-testing-strategy.md, principle 4).

const STACK_NAME = ".weavie-inline-stack-name";

// The changed file currently in the navigator (its stacked label), trimmed; "" before the first diff renders.
async function currentFile(page: Page): Promise<string> {
  return (await page.locator(STACK_NAME).textContent())?.trim() ?? "";
}

// Step the navigator to the next changed file (Ctrl/Cmd+→) and resolve once the label has advanced off `from`.
// Returns the file now in view. The editor must already hold focus so the chord reaches the navigator.
async function stepToNextFile(page: Page, from: string): Promise<string> {
  await page.keyboard.press("ControlOrMeta+ArrowRight");
  await expect.poll(() => currentFile(page)).not.toBe(from);
  return currentFile(page);
}

// The set of changed files the navigator cycles through, gathered by walking → until it loops back to the
// first. Event-based, so it neither misses a file under load nor wastes time waiting on a fixed delay.
export async function collectChangedFiles(page: Page): Promise<Set<string>> {
  await page.locator(".monaco-editor").first().click();
  await expect(page.locator(STACK_NAME)).not.toHaveText("");
  const first = await currentFile(page);
  const seen = new Set<string>([first]);
  for (let i = 0; i < 12; i++) {
    const next = await stepToNextFile(page, await currentFile(page));
    if (next === first) {
      break; // cycled back — every changed file has been seen
    }
    seen.add(next);
  }
  return seen;
}

// Walk the navigator forward until `target` is in view, then assert it arrived. Replaces the per-step
// fixed-delay loops the PR specs used to reach a specific changed file.
export async function walkToChangedFile(page: Page, target: string): Promise<void> {
  await page.locator(".monaco-editor").first().click();
  for (let i = 0; i < 6 && (await currentFile(page)) !== target; i++) {
    await stepToNextFile(page, await currentFile(page));
  }
  await expect(page.locator(STACK_NAME)).toHaveText(target);
}
