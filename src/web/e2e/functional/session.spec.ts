import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import {
  activeSessionSlot,
  createSession,
  openFile,
  runCommand,
  waitForSessionSwitch,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

// Session lifecycle through the rail + commands: create (fork a worktree session off the current one),
// switch, unload, and reopen. Sessions and worktrees are a HostCore concern that differs structurally on
// remote (the Runner provisions the worktree), so this runs @cross.
//
// The `weavie` fixture captures console-errors.txt/weavie-host.log/viewport-layout.json around setup as well
// as the test body (try/finally around setup+use), so a boot-time `#splash` timeout lands with its diagnostic
// datum attached rather than none.
test("create, switch, unload, and reopen sessions @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);
  const initialSlot = await activeSessionSlot(page);

  // Create: forking spins up a second session on its own worktree, which becomes active.
  await createSession(page, { branch: "e2e/session-lifecycle", provider: "claude" });
  await expect(chips).toHaveCount(2);
  await waitForSessionSwitch(page, initialSlot);

  // Switch: clicking the first chip makes it active.
  await chips.first().click();
  await expect(chips.first()).toHaveClass(/\bactive\b/);

  // Switch via the keyboard (Next Session) — still exactly one active session.
  const beforeNext = await activeSessionSlot(page);
  await runCommand(page, "Next Session");
  await waitForSessionSwitch(page, beforeNext);

  // Unload: the active session's backend is torn down; its chip goes faded/unloaded.
  await runCommand(page, "Unload Session");
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(1);
  await expect(
    page.locator(".toast", { hasText: "was unloaded. Its worktree was kept." }),
  ).toHaveCount(1);
  await expect(page.locator(".toast", { hasText: "Unloading the session" })).toHaveCount(0);

  // Reopen: clicking an unloaded chip loads it again (no longer unloaded).
  await page.locator(".session-chip.unloaded").click();
  await expect(page.locator(".session-chip.unloaded")).toHaveCount(0);
});

test("the prompt-free action opens an existing branch", async ({ page }) => {
  const initialSlot = await activeSessionSlot(page);
  await runCommand(page, "Sessions");
  const inbox = page.locator(".session-inbox");
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    initialSlot,
  );
  const openGroup = inbox.getByRole("region", { name: "Open an existing branch" });
  const branch = await inbox.locator(".session-inbox-row.active strong").innerText();

  await inbox
    .getByRole("textbox", { name: "Prompt for a new session" })
    .fill("This draft must not become existing-session input");
  await openGroup.getByRole("combobox", { name: "Existing branch for the session" }).fill(branch);
  await openGroup.getByRole("button", { name: "Open", exact: true }).click();

  await expect(inbox).toBeHidden();
  await expect(page.locator(".session-chip.active")).toHaveAttribute(
    "data-session-slot",
    initialSlot,
  );
});

test("Shift+Enter starts a named session from the prompt", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);
  await runCommand(page, "Sessions");

  const inbox = page.locator(".session-inbox");
  const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
  await expect(branch).toHaveAttribute("autocapitalize", "none");
  await expect(branch).toHaveAttribute("autocomplete", "off");
  await expect(branch).toHaveAttribute("spellcheck", "false");
  await branch.fill("e2e/shift-enter-session");

  const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  await prompt.fill("Start this session from the keyboard");
  const start = inbox.getByRole("button", { name: "Start", exact: true });
  await expect(start).toBeEnabled();
  await expect(start).toHaveAttribute("title", /Shift\+Enter/);
  await prompt.press("Shift+Enter");

  await expect(inbox).toBeHidden();
  await expect(chips).toHaveCount(2);
});

// The composer's prompt is the session's opening turn, so it has to reach the agent itself. It rides the
// agent's launch rather than being typed into a starting TUI, which discards or re-frames written input and
// used to swallow the prompt outright. The empty script only turns the fake's log on (it echoes its launch).
test.describe("new-session prompt", () => {
  test.use({ fakeScript: { steps: [] } });

  test("reaches the agent it starts", async ({ page, weavie }) => {
    await runCommand(page, "Sessions");
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("claude");
    await inbox
      .getByRole("textbox", { name: "Branch for the new session" })
      .fill("e2e/session-prompt");
    await inbox
      .getByRole("textbox", { name: "Prompt for a new session" })
      .fill("don't lose this prompt");
    await inbox.getByRole("button", { name: "Start", exact: true }).click();
    await expect(inbox).toBeHidden();

    await expect
      .poll(() => weavie.fakeLog(), { timeout: 30_000 })
      .toContain("prompt don't lose this prompt");
  });
});

test("reload restores the client-selected stable session slot @cross", async ({ page, weavie }) => {
  const initialSlot = await activeSessionSlot(page);
  await createSession(page, { branch: "e2e/session-reload", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  const slot = await waitForSessionSwitch(page, initialSlot);
  await expect
    .poll(async () => {
      const json = await readFile(join(weavie.home, ".weavie", "rail-state.json"), "utf8").catch(
        () => null,
      );
      if (json === null) {
        return null;
      }
      const state = JSON.parse(json) as { selected?: unknown };
      return state.selected;
    })
    .toEqual({ backendId: "local", slot });

  await page.reload({ waitUntil: "domcontentloaded" });
  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });

  await expect(page.locator(".session-chip.active")).toHaveAttribute("data-session-slot", slot);
});

// Delete a (clean) session: right-click its chip → Delete… → confirm. A freshly forked worktree has no
// changes, so the confirm dialog is the plain clean-state variant (single danger button, no checkbox).
test("delete a session removes its chip @cross", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await expect(chips).toHaveCount(1);
  await createSession(page, { branch: "e2e/session-delete", provider: "claude" });
  await expect(chips).toHaveCount(2);

  await chips.nth(1).click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toBeVisible();
  await dialog.locator(".confirm-btn-danger").click();

  await expect(chips).toHaveCount(1);
  await expect(
    page.locator(".toast", { hasText: "was deleted. Its branch was kept." }),
  ).toHaveCount(1);
  await expect(page.locator(".toast", { hasText: "Deleting session" })).toHaveCount(0);
});

test("delete confirmation names tracked and untracked work that will be lost @cross", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");
  await createSession(page, { branch: "e2e/session-dirty-delete", provider: "claude" });
  await expect(chips).toHaveCount(2);
  const [worktree] = sessionWorktrees(weavie.workspace);
  if (worktree === undefined) {
    throw new Error("forked session did not create a git worktree");
  }
  await Promise.all([
    writeFile(join(worktree, "hello.ts"), "tracked edit\n"),
    writeFile(join(worktree, "scratch.txt"), "untracked work\n"),
  ]);

  await chips.nth(1).click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog.locator(".confirm-file-list")).toContainText("hello.ts");
  await expect(dialog.locator(".confirm-file-list")).toContainText("scratch.txt");
  await expect(dialog.locator(".confirm-check input")).not.toBeChecked();
  await expect(dialog.locator(".confirm-btn-danger")).toBeDisabled();
});

test("deleting the workspace session keeps its checkout and creates a replacement", async ({
  page,
  weavie,
}) => {
  const chips = page.locator(".session-chip");
  const deletedId = await activeSessionSlot(page);

  await chips.first().click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();

  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toContainText("Its checkout and files remain on disk.");
  await expect(dialog).not.toContainText("Remove the worktree");
  await dialog.locator(".confirm-btn-danger").click();

  await expect(chips).toHaveCount(1);
  await expect(chips.first()).not.toHaveAttribute("data-session-slot", deletedId);
  await expect(page.locator(".toast", { hasText: "was deleted." })).toHaveCount(1);
  expect(await readFile(join(weavie.workspace, "hello.ts"), "utf8")).toContain("greet");
});

// Ctrl+Tab / Ctrl+Shift+Tab must step exactly one chip per press. Two sessions can't catch a step that walks
// from a stale origin — next and prev are the same hop there — so this cycles three, both ways, through a wrap.
test("keyboard session cycling steps one chip per press in both directions", async ({ page }) => {
  const chips = page.locator(".session-chip");
  await createSession(page, { branch: "e2e/cycle-second", provider: "claude" });
  await createSession(page, { branch: "e2e/cycle-third", provider: "claude" });
  await expect(chips).toHaveCount(3);

  // Park focus in the shell pane so the session binding owns the chord directly.
  const shell = page.locator('.terminal-surface[data-kind="terminal:shell"]');
  await shell.locator(".shell-tab-main").click();
  const slots = await chips.evaluateAll((rail) =>
    rail.map((chip) => (chip as HTMLElement).dataset.sessionSlot ?? ""),
  );
  const expectActive = (slot: string): Promise<void> =>
    expect(page.locator(".session-chip.active")).toHaveAttribute("data-session-slot", slot);

  let index = slots.indexOf(await activeSessionSlot(page));
  for (const delta of [1, 1, 1, -1, -1, -1]) {
    await page.keyboard.press(delta > 0 ? "Control+Tab" : "Control+Shift+Tab");
    index = (index + delta + slots.length) % slots.length;
    await expectActive(slots[index] as string);
  }
});

// Ctrl+Tab is the editor's "next file" while the editor holds focus, and session cycling everywhere else.
// With one file open the editor has no tab to step to, so the press has to reach cycling instead of dying
// between the two — the dead key that made switching sessions take a second press.
test("Ctrl+Tab reaches session cycling when the focused editor has no other tab", async ({
  page,
}) => {
  await createSession(page, { branch: "e2e/editor-chord", provider: "claude" });
  await expect(page.locator(".session-chip")).toHaveCount(2);
  const editor = page.locator('.editor-surface[data-kind="editor"]');
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").click();
  await expect(editor).toHaveClass(/\bactive\b/);
  await expect(page.locator(".editor-tab")).toHaveCount(1);

  const before = await activeSessionSlot(page);
  await page.keyboard.press("Control+Tab");
  await waitForSessionSwitch(page, before);

  // A second file gives the editor somewhere to go, so the chord is its own again: the file changes and the
  // session doesn't.
  await openFile(page, "hello.ts");
  await openFile(page, "notes.txt");
  await page.locator(".monaco-editor .view-lines").click();
  await expect(editor).toHaveClass(/\bactive\b/);
  const session = await activeSessionSlot(page);
  await page.keyboard.press("Control+Tab");
  await expect(page.locator(".editor-tab.active .editor-tab-label")).toHaveText("hello.ts");
  expect(await activeSessionSlot(page)).toBe(session);
});
