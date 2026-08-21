import { expect, type Page } from "@playwright/test";
import { mediaTypeOf } from "../../src/editor/media/media-types";
import type { WeavieWindow } from "./weavie-window";

// The editor chunk is deferred past the shell's first paint, so it isn't up when the splash clears — it stamps
// `data-ready` on `.editor` once Monaco is live. Editor-driving helpers wait on this; non-editor tests don't.
// The ceiling tracks the app's own EDITOR_INIT_MS cold-boot deadline: on the remote worker hop under a loaded CI
// box the boot legitimately runs tens of seconds, so waiting less than the app allows would flake the wait, not
// the app. Pair with test.slow() on @cross editor tests so the whole test has budget beyond just this wait.
export async function awaitEditorReady(page: Page): Promise<void> {
  await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true", { timeout: 60_000 });
}

// The published `--editor-font-family` CSS var updates synchronously, but Monaco's own remeasure against
// the loaded webfont (triggered off `document.fonts` readiness) is scheduled, not same-tick — so a rendered
// `.view-line`'s computed font can still lag the published value right after a file/pane opens. Wait for the
// webfont load and give the editor a frame to lay out against it before reading computed typography.
export async function awaitFontsSettled(page: Page): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  await page.evaluate(
    () => new Promise<void>((r) => requestAnimationFrame(() => requestAnimationFrame(() => r()))),
  );
}

export async function activeSessionSlot(page: Page): Promise<string> {
  const active = page.locator(".session-chip.active");
  await expect(active).toHaveCount(1);
  const slot = await active.getAttribute("data-session-slot");
  if (slot === null) {
    throw new Error("The selected session chip has no slot.");
  }
  return slot;
}

export async function waitForSessionSwitch(page: Page, previousSlot: string): Promise<string> {
  await expect
    .poll(async () => {
      const slot = await activeSessionSlot(page);
      return slot === previousSlot ? null : slot;
    })
    .not.toBeNull();
  return activeSessionSlot(page);
}

// Open a workspace file through the omnibar's "Go to File" and wait until the editor is ACTUALLY showing it.
// The first fuzzy match is auto-selected, so typing the name and pressing Enter opens it.
export async function openFile(page: Page, name: string): Promise<void> {
  await awaitEditorReady(page);
  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill(name);
  await expect(page.locator(".tb-omnibar-row", { hasText: name }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(page.locator(".editor-tab", { hasText: name })).toBeVisible();
  // The tab appears (and its active state + the current-file flip) BEFORE the Monaco model swap lands — that
  // swap is an async host round-trip. Typing in the gap leaks into the outgoing model, so wait for the editor
  // to actually bind this file (data-active-file, stamped on the real swap). Media files never bind a model.
  if (mediaTypeOf(name) === null) {
    const escaped = name.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"); // every regex metachar, backslash included
    await expect(page.locator(".editor")).toHaveAttribute(
      "data-active-file",
      new RegExp(`[\\\\/]${escaped}$`),
    );
    await awaitEditorLaidOut(page);
  }
}

// Monaco sizes its viewport as max(5, container.clientHeight), so a container that is momentarily 0-height when
// it measures leaves it latched at 5px — rendering the first line and NOTHING else. Every later line is then
// simply absent from the DOM, and a locator addressing one waits out the whole test timeout with nothing to
// report but "waiting for locator". Binding the model says which file the editor holds, not that it has room to
// draw it, so wait for the viewport to agree with its container before a spec addresses rendered text.
//
// Height agreement alone isn't sufficient proof the clamp has cleared: `editor-peek-definition.spec.ts`'s
// multicursor test kept flaking on Windows CI (runs 31993224310, 32096266021, 32104522458, 32333399943 — the
// last one after this exact height check was already gating every call site) with the documented fingerprint —
// `renderedLines` showing a single, often-blank line at teardown while `.editor`/`.monaco` read a healthy size.
// A height-only check can pass on a stale read (Monaco's `getLayoutInfo()` reflects the size *at measurement
// time*, not that the clamped single-line viewport has actually re-rendered every line back in). So also require
// the DOM to hold more than the clamp's one-line placeholder whenever the model has more than one line —
// checking the actual rendered output, not a derived number that can agree while the render is still catching up.
//
// The poll needs more runway than the suite's default `expect.timeout` (playwright.config.ts): that default
// (30s on Windows/macOS) is what this test's OWN PR CI run (32335659526) hit two fresh failures against within
// hours of landing — the poll timed out at 30s on the exact -1 (clamp-still-active) signature, where previously
// the failure surfaced later, in the click()'s own actionability wait, which isn't bound by `expect.timeout` and
// so had the full ~60s test budget to let a slow-but-genuine recovery finish. Matching that budget here (instead
// of inheriting the shorter global default) restores the runway this wait always implicitly had, rather than
// quietly shrinking it as a side effect of making the check stricter.
export async function awaitEditorLaidOut(page: Page): Promise<void> {
  await expect
    .poll(
      () =>
        page.evaluate(() => {
          const editor = (window as WeavieWindow).__WEAVIE_EDITOR__;
          const container = document.querySelector(".editor-surface .editor");
          if (editor === undefined || container === null) {
            return null;
          }
          const heightDiff = editor.getLayoutInfo().height - container.clientHeight;
          if (heightDiff !== 0) {
            return heightDiff;
          }
          const modelLineCount = editor.getModel()?.getLineCount() ?? 0;
          const renderedLineCount = document.querySelectorAll(".view-line").length;
          return modelLineCount > 1 && renderedLineCount <= 1 ? -1 : 0;
        }),
      {
        message: "Monaco's viewport never matched its container (editor stuck at the 5px clamp)",
        timeout: process.platform === "linux" ? 15_000 : 45_000,
      },
    )
    .toBe(0);
}

// Run a command through the command palette (Show All Commands), matching by title text. Exercises the
// same keyboard path a user would: $mod+Shift+p, type, Enter on the first match.
export async function runCommand(page: Page, title: string): Promise<void> {
  const box = page.locator(".tb-omnibar-box");
  // Ensure the palette is closed first, so the open shortcut doesn't toggle a still-open palette shut
  // (it stays open briefly after a prior command runs).
  await page.keyboard.press("Escape");
  await expect(box).not.toHaveClass(/\bopen\b/);
  // Open it — retried because a focused pane (xterm/Monaco) occasionally swallows the first chord under
  // load, so the keypress doesn't reach the global handler.
  await expect(async () => {
    await page.keyboard.press("ControlOrMeta+Shift+p");
    await expect(box).toHaveClass(/\bopen\b/, { timeout: 1000 });
  }).toPass({ timeout: 10_000 });
  // Command mode is signalled by a leading ">"; keep it on the filled value (a bare fill would drop to
  // file search).
  await page.locator(".tb-omnibar-input").fill(`>${title}`);
  await expect(page.locator(".tb-omnibar-row", { hasText: title }).first()).toBeVisible();
  await page.locator(".tb-omnibar-input").press("Enter");
  await expect(box).not.toHaveClass(/\bopen\b/);
}

export async function allowAutomaticInference(page: Page): Promise<void> {
  const offer = page.locator(".toast", { hasText: "Let Weavie use automatic inference" });
  await expect(offer).toBeVisible();
  await expect(offer).not.toHaveClass(/toast-timed/);
  await expect(offer.getByRole("button", { name: /Allow/ })).toContainText(
    /Ctrl\+Alt\+I|⌘\+Alt\+I/,
  );
  await offer.getByRole("button", { name: /Allow/ }).click();
  await expect(offer).toHaveCount(0);
  await expect(page.locator(".toast", { hasText: "Automatic inference enabled." })).toBeVisible();
}

export async function createSession(
  page: Page,
  seed: { branch: string; provider: string },
): Promise<void> {
  await runCommand(page, "Sessions");
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption(seed.provider);
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill(seed.branch);
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();
}

// Click into the editor to give it keyboard focus. Targets `.monaco-editor`, which is exactly the viewport, and
// never `.view-lines`: with `scrollBeyondLastLine` that container is taller than the viewport, so Playwright
// scrolls its centre point into view before clicking — and when the editor is momentarily collapsed (a 0-height
// container clamps Monaco's viewport to 5px) that point is off-screen, so the scroll lands on Monaco's maximum
// offset, leaving the editor parked past the last line and rendering one blank line long after the collapse
// heals. A viewport-sized target is always in view, so clicking it never scrolls.
// See docs/specs/e2e-flake-analysis.md.
export async function clickIntoEditor(page: Page): Promise<void> {
  await awaitEditorReady(page);
  await page.locator(".monaco-editor").first().click();
}

// Type text at the current caret in the focused Monaco editor.
export async function typeInEditor(page: Page, text: string): Promise<void> {
  await clickIntoEditor(page);
  await page.keyboard.type(text);
}
