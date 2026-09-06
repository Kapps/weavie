import { expect, type Page } from "@playwright/test";
import { awaitEditorReady } from "./actions";

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

export async function focusEditor(page: Page): Promise<void> {
  await awaitEditorReady(page);
  await page.locator(".monaco-editor").first().click();
  await expect
    .poll(
      () =>
        page.evaluate(
          () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
        ),
      { timeout: 1_000 },
    )
    .toBe("editor");
}

async function stepToNextFile(page: Page, from: string): Promise<string> {
  await focusEditor(page);
  await page.keyboard.press(navChord("ArrowRight"));
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
}

// Complete a real navigation cycle; an unexpected cycle fails instead of returning a partial file set.
async function* changedFiles(page: Page): AsyncGenerator<string> {
  await focusEditor(page);
  const first = await settledFile(page);
  const seen = new Set<string>();
  let current = first;
  do {
    if (seen.has(current)) {
      throw new Error(`navigator repeated ${current} without returning to ${first}`);
    }
    seen.add(current);
    yield current;
    current = await stepToNextFile(page, current);
  } while (current !== first);
}

export async function collectChangedFiles(page: Page): Promise<Set<string>> {
  const files = new Set<string>();
  for await (const file of changedFiles(page)) {
    files.add(file);
  }
  return files;
}

export async function awaitReviewSet(page: Page, files: string[]): Promise<void> {
  await expect
    .poll(
      () =>
        page.evaluate(
          () =>
            window.__WEAVIE_REVIEW__?.files.map((path) => path.split(/[\\/]/).pop()).sort() ?? null,
        ),
      { timeout: 20_000 },
    )
    .toEqual([...files].sort());
}

export async function walkToChangedFile(page: Page, target: string): Promise<void> {
  for await (const file of changedFiles(page)) {
    if (file === target) {
      await expect(page.locator(STACK_NAME)).toHaveText(target);
      return;
    }
  }
  throw new Error(`navigator completed a cycle without ${target}`);
}
