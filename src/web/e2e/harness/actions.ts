import { expect, type Locator, type Page } from "@playwright/test";
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

export async function clickOmnibarRowThroughToast(
  page: Page,
  rows: Locator,
  toast: Locator,
): Promise<void> {
  const toastElement = await toast.elementHandle();
  if (toastElement === null) {
    throw new Error("The toast must be attached.");
  }
  const covered = await rows.evaluateAll((elements, notification) => {
    const toastBounds = notification.getBoundingClientRect();
    for (const [index, element] of elements.entries()) {
      const rowBounds = element.getBoundingClientRect();
      const left = Math.max(rowBounds.left, toastBounds.left);
      const right = Math.min(rowBounds.right, toastBounds.right);
      const top = Math.max(rowBounds.top, toastBounds.top);
      const bottom = Math.min(rowBounds.bottom, toastBounds.bottom);
      if (right > left && bottom > top) {
        return { center: { x: (left + right) / 2, y: (top + bottom) / 2 }, index };
      }
    }
    return null;
  }, toastElement);
  await toastElement.dispose();
  if (covered === null) {
    throw new Error("No omnibar result is covered by the toast.");
  }
  const row = rows.nth(covered.index);
  const ownsHit = await row.evaluate((element, center) => {
    return document.elementFromPoint(center.x, center.y)?.closest(".tb-omnibar-row") === element;
  }, covered.center);
  expect(ownsHit).toBe(true);
  await page.mouse.click(covered.center.x, covered.center.y);
}

// Wait until Monaco has actually drawn the file, not merely bound it. Two things can leave the editor holding
// a model it isn't showing: the viewport clamped to `max(5, container.clientHeight)` because the container was
// momentarily 0-height, and a render that hasn't caught up with a viewport that already reports the right size.
// Either way the DOM holds one line and every locator addressing another waits out the whole test budget with
// nothing to report, so check the rendered output directly and not just the derived height.
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
        message:
          "Monaco never drew the file's lines (viewport clamped, or the render never landed)",
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
  await expect(page.locator(`.session-chip.active[title^="${seed.branch} —"]`)).toBeVisible();
}

// Asserts a reveal landed: the editor is showing `file` (a workspace-relative path) with the caret on `line`.
export async function expectRevealed(page: Page, file: string, line: number): Promise<void> {
  const pattern = file
    .split("/")
    .map((part) => part.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"))
    .join("[\\\\/]");
  await expect(page.locator(".editor")).toHaveAttribute(
    "data-active-file",
    new RegExp(`[\\\\/]${pattern}$`),
  );
  await expect
    .poll(() => page.evaluate(() => window.__WEAVIE_EDITOR__?.getPosition()?.lineNumber))
    .toBe(line);
}

// Click into Monaco: focuses the editor pane and puts the caret on the first rendered line.
//
// The target is a `.view-line`, never the `.view-lines` container. Monaco sizes that container to the whole
// scrollable content — with `scrollBeyondLastLine` a 7-line file is 853px tall inside a 709px viewport — and
// Playwright reveals an element before clicking it. The browser satisfies that by natively scrolling the
// `overflow:hidden` ancestor, which Monaco deliberately folds back into its own scroll position
// (`editorScrollbar.ts`'s `onBrowserDesperateReveal`). The editor ends scrolled to its maximum, rendering only
// the file's last line, and every later locator for any other line matches nothing. A single `.view-line` is
// one line tall and always inside the viewport, so the reveal is a no-op.
export async function clickIntoEditor(page: Page): Promise<void> {
  await awaitEditorReady(page);
  // Near the line's start, not its centre: a line's box spans the whole content width, so on a short line the
  // centre lands on the git-blame annotation injected after the code — which owns that click and swallows it,
  // leaving the editor unfocused.
  await page
    .locator(".monaco-editor .view-line")
    .first()
    .click({ position: { x: 4, y: 4 } });
}

// Type text at the current caret in the focused Monaco editor. Callers place the caret themselves (a click
// into the editor, Home/End); typing does not move it first.
export async function typeInEditor(page: Page, text: string): Promise<void> {
  await awaitEditorReady(page);
  await page.keyboard.type(text);
}
