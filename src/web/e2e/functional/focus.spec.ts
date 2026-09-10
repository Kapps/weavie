import {
  activeSessionSlot,
  clickIntoEditor,
  createSession,
  openFile,
  pressDocumentStart,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { withHeldAnimationFrames } from "../harness/animation-frames";
import { expect, test } from "../harness/fixtures";

// Which pane currently holds DOM focus, as the data-kind of the focused element's surface. This is the ground
// truth for "where will my keystrokes go" — independent of the visual `.active` highlight.
const focusedKind = (page: import("@playwright/test").Page): Promise<string | null> =>
  page.evaluate(
    () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
  );

// Regression: clicking a pane's chrome must move focus into that pane. The shell tab activates its xterm;
// otherwise DOM focus can stay in the editor and the next keystroke goes to Monaco.
test("terminal chrome transfers focus and typing between editor, shell, and agent", async ({
  page,
}) => {
  const editor = page.locator('.editor-surface[data-kind="editor"]');
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const claude = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const viewLines = page.locator(".monaco-editor .view-lines").first();

  // Start with the editor genuinely focused: open a file and click into Monaco.
  await openFile(page, "hello.ts");
  await clickIntoEditor(page);
  await pressDocumentStart(page);
  await expect(editor).toHaveClass(/\bactive\b/);

  await test.step("shell tab transfers active styling and DOM focus", async () => {
    await shell.locator(".shell-tab-main").click();

    // Focus moved: the terminal is highlighted, the editor isn't, and DOM focus is inside the terminal.
    await expect(shell).toHaveClass(/\bactive\b/);
    await expect(editor).not.toHaveClass(/\bactive\b/);
    expect(await focusedKind(page)).toBe("terminal:shell");
  });

  await test.step("typing leaves the editor source intact", async () => {
    await page.keyboard.type("focusXYZZY");

    // The editor never received the keystrokes — the marker is absent and the seeded source is intact.
    await expect(viewLines).not.toContainText("focusXYZZY");
    await expect(viewLines).toContainText("greet");
  });

  await test.step("agent chrome transfers focus out of the shell", async () => {
    await claude.locator(".pane-head").click();
    await expect(claude).toHaveClass(/\bactive\b/);
    await expect(shell).not.toHaveClass(/\bactive\b/);
    expect(await focusedKind(page)).toBe("terminal:claude");
  });
});

test("creating a session focuses its agent while ordinary session switching does not force it", async ({
  page,
}) => {
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  const initialSlot = await activeSessionSlot(page);
  await shell.locator(".shell-tab-main").click();
  expect(await focusedKind(page)).toBe("terminal:shell");

  await runCommand(page, "Sessions");
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
  await inbox
    .getByRole("textbox", { name: "Branch for the new session" })
    .fill("e2e/session-focus");
  const start = await inbox.getByRole("button", { name: "Start", exact: true }).boundingBox();
  expect(start).not.toBeNull();
  await withHeldAnimationFrames(page, async () => {
    await page.mouse.click(start!.x + start!.width / 2, start!.y + start!.height / 2);
    await waitForSessionSwitch(page, initialSlot);
    await expect(inbox).toBeHidden();
    await expect(
      page.locator('[data-kind="terminal:claude"] .term-host:not(.hidden) .xterm-helper-textarea'),
    ).toBeFocused();
  });

  await shell.locator(".shell-tab-main").click();
  const forkedSlot = await activeSessionSlot(page);
  await page.keyboard.press("Control+Tab");
  await waitForSessionSwitch(page, forkedSlot);
  await expect(shell).toHaveClass(/\bactive\b/);
  // Focus travels with the switch into the same pane of the session now in front — it neither jumps to the
  // agent nor is left behind on the outgoing session's pane.
  await expect.poll(() => focusedKind(page)).toBe("terminal:shell");
});

// A switch swaps which session's panes are on screen. Focus has to travel with it: otherwise the caret stays
// on the element that just went away (landing on nothing), so the incoming pane paints itself active while
// typing goes nowhere — and the stale focus state routes Ctrl+Tab to a pane the user has already left.
test("typing lands in the session a keyboard switch brings up, with no click first", async ({
  page,
}) => {
  await createSession(page, { branch: "e2e/focus-carry-a", provider: "fake-acp" });
  await createSession(page, { branch: "e2e/focus-carry-b", provider: "fake-acp" });
  const composer = page.locator('[data-surface="structured-agent"] [data-agent-composer] textarea');
  await composer.click();
  await composer.fill("session b draft");

  await withHeldAnimationFrames(page, async () => {
    await page.keyboard.press("Control+Shift+Tab");
    await expect(page.locator('.session-chip.active[title^="e2e/focus-carry-a —"]')).toBeVisible();
    await page.keyboard.type("typed without clicking");
    await expect(composer).toHaveValue("typed without clicking");
    await expect(composer).toBeFocused();
  });
});
