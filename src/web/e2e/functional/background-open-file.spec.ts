import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, setFakeAcpMode, submitAcpDraft } from "../harness/acp-session";
import { activeSessionSlot, openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";
import { focusEditor } from "../harness/navigator";
import { persistedActiveTabs } from "../harness/persisted-sessions";

// A session the user isn't viewing can open files for them: the tab waits there, and the session they are in keeps
// its selection, tabs, and keyboard focus. See docs/specs/workflows.md.

const openPreview = (name: string): string =>
  `mcp:openFile:${JSON.stringify({ filePath: name, preview: true, rendered: true })}`;

const focused = (page: Page): Promise<string | undefined> =>
  page.evaluate(() => document.activeElement?.outerHTML.slice(0, 200));

test("openFile in a background session waits there without disturbing the viewed session", async ({
  page,
  weavie,
}) => {
  // Without steering the opens queue behind the held turn, so they run only once the session is in the background.
  await setFakeAcpMode(page, weavie.home, "no-steering");
  const home = await activeSessionSlot(page);
  await writeFile(join(weavie.workspace, "home.md"), "# home\n");
  await openFile(page, "home.md");
  const surface = await createAcpSession(page, "feature/background-open");
  const background = await activeSessionSlot(page);
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(worktree, "first.md"), "# first\n");
  await writeFile(join(worktree, "second.md"), "# second\n");
  // Pinned before the turn starts: the palette's Escape would interrupt a running turn.
  await runCommand(page, "Pin Session Panel");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();
  for (const name of ["first.md", "second.md"]) {
    await submitAcpDraft(surface, openPreview(name));
    await expect(surface.locator(".agent-compose-queued")).toContainText(name);
  }
  await expect(surface.locator(".agent-entry-message", { hasText: "FILE_OPENED" })).toHaveCount(0);

  const row = (slot: string) => page.locator(`.session-panel-row[data-session-slot="${slot}"]`);
  await row(home).click();
  await expect(row(home)).toHaveClass(/\bactive\b/);
  await focusEditor(page);
  const tabs = page.locator(".editor-tab");
  await expect(tabs).toHaveCount(1);
  const homeFocus = await focused(page);

  await writeFile(join(worktree, "release-hold"), "");
  await expect
    .poll(() => persistedActiveTabs(weavie.home).some((path) => path.endsWith("second.md")))
    .toBe(true);

  await expect(row(home)).toHaveClass(/\bactive\b/);
  await expect(tabs).toHaveCount(1);
  await expect(page.locator(".editor-tab.active")).toContainText("home.md");
  expect(await focused(page)).toBe(homeFocus);

  await row(background).click();
  await expect(row(background)).toHaveClass(/\bactive\b/);
  await expect(tabs).toHaveCount(1);
  await expect(page.locator(".editor-tab.preview.active")).toContainText("second.md");
  await expect(page.locator(".editor-preview h1")).toHaveText("second");
  await expect(surface.locator(".agent-entry-message", { hasText: "FILE_OPENED" })).toHaveCount(2);

  // In the viewed session the file shows, the composer keeps the keyboard, and a bad path reaches the agent.
  const composer = surface.locator("[data-agent-composer] textarea");
  await submitAcpDraft(surface, openPreview("first.md"));
  await expect(page.locator(".editor-tab.preview.active")).toContainText("first.md");
  await expect(composer).toBeFocused();
  await submitAcpDraft(surface, openPreview("ghost.md"));
  await expect(surface.locator(".agent-entry-message").last()).toContainText("openFile failed");
});

test("openFile leaves a user typing in the viewed session's preview tab where they are", async ({
  page,
  weavie,
}) => {
  await setFakeAcpMode(page, weavie.home, "no-steering");
  const surface = await createAcpSession(page, "feature/typing-open");
  const worktree = sessionWorktrees(weavie.workspace)[0]!;
  await writeFile(join(worktree, "mine.md"), "# mine\n");
  await writeFile(join(worktree, "agent.md"), "# agent\n");
  await submitAcpDraft(
    surface,
    `mcp:openFile:${JSON.stringify({ filePath: "mine.md", preview: true })}`,
  );
  const mine = page.locator(".editor-tab.preview.active", { hasText: "mine.md" });
  await expect(mine).toBeVisible();

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();
  for (const name of ["agent.md", "mine.md"]) {
    await submitAcpDraft(surface, openPreview(name));
    await expect(surface.locator(".agent-compose-queued")).toContainText(name);
  }
  // Focused but unedited: an edit would promote the preview tab, and this open must not take it.
  await focusEditor(page);
  await writeFile(join(worktree, "release-hold"), "");

  await expect(surface.locator(".agent-entry-message", { hasText: "FILE_OPENED" })).toHaveCount(3);
  await expect(page.locator(".editor-tab")).toHaveCount(2);
  await expect(page.locator(".editor-tab.active")).toContainText("mine.md");
  await expect(page.locator(".editor-tab.preview")).toContainText("agent.md");
  await page.keyboard.type("uninterrupted");
  await expect(page.locator(".monaco-editor .view-lines").first()).toContainText("uninterrupted");
});
