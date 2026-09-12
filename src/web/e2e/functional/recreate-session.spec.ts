import { readFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import {
  activeSessionSlot,
  clickIntoEditor,
  openFile,
  runCommand,
  typeInEditor,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

async function chooseAgent(page: Page, provider: string) {
  const dialog = page.getByRole("dialog", { name: "Recreate with…" });
  const picker = dialog.getByRole("combobox", { name: "Agent", exact: true });
  await expect(picker).toBeFocused();
  await picker.selectOption(provider);
  await dialog.getByRole("button", { name: "Continue", exact: true }).click();
  await expect(dialog).toContainText("Previous conversations won't be resumed.");
  return dialog;
}

async function recreate(page: Page, provider: string) {
  await runCommand(page, "Recreate with");
  const dialog = await chooseAgent(page, provider);
  await dialog.getByRole("button", { name: "Recreate session", exact: true }).focus();
  await page.keyboard.press("Enter");
  await expect(dialog).toBeHidden();
}

test("main session can change agents and returning starts fresh", async ({ page, weavie }) => {
  const slot = await activeSessionSlot(page);
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await typeInEditor(page, "retained main edit");
  await page.keyboard.press("ControlOrMeta+s");
  await expect
    .poll(() => readFile(join(weavie.workspace, "notes.txt"), "utf8"))
    .toContain("retained main edit");

  await page.locator(".session-chip.active").click({ button: "right" });
  await page.getByRole("menuitem", { name: /Recreate with/ }).click();
  const dialog = await chooseAgent(page, "fake-acp");
  await dialog.getByRole("button", { name: "Recreate session", exact: true }).click();
  await expect(dialog).toBeHidden();
  const surface = page.locator('[data-surface="structured-agent"]');
  await submitAcpDraft(surface, "old main conversation");
  await expect(surface).toContainText("echo: old main conversation");
  expect(await activeSessionSlot(page)).toBe(slot);

  await runCommand(page, "Recreate with");
  await page.getByRole("dialog").getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(surface).toContainText("echo: old main conversation");
  await runCommand(page, "Recreate with");
  const cancelled = await chooseAgent(page, "claude");
  await cancelled.getByRole("button", { name: "Cancel", exact: true }).click();
  await expect(surface).toContainText("echo: old main conversation");

  await recreate(page, "claude");
  await expect(surface).toBeHidden();
  await recreate(page, "fake-acp");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(surface.locator(".agent-entry")).toHaveCount(0);
  await submitAcpDraft(surface, "new main conversation");
  await expect(surface).toContainText("echo: new main conversation");
  await expect(surface).not.toContainText("old main conversation");
  expect(await activeSessionSlot(page)).toBe(slot);
  await expect(page.locator(".editor-tab", { hasText: "notes.txt" })).toBeVisible();
  expect(await readFile(join(weavie.workspace, "notes.txt"), "utf8")).toContain(
    "retained main edit",
  );
});

test("worktree recreation with the same agent stops its turn and retains edits", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "recreate-worktree");
  const slot = await activeSessionSlot(page);
  await openFile(page, "notes.txt");
  await clickIntoEditor(page);
  await typeInEditor(page, "retained worktree edit");
  await page.keyboard.press("ControlOrMeta+s");
  const path = join(sessionWorktrees(weavie.workspace)[0]!, "notes.txt");
  await expect.poll(() => readFile(path, "utf8")).toContain("retained worktree edit");
  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await recreate(page, "fake-acp");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(surface.locator(".agent-entry")).toHaveCount(0);
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  expect(await activeSessionSlot(page)).toBe(slot);
  await expect(page.locator(".editor-tab", { hasText: "notes.txt" })).toBeVisible();
  expect(await readFile(path, "utf8")).toContain("retained worktree edit");
  await submitAcpDraft(surface, "fresh worktree conversation");
  await expect(surface).toContainText("echo: fresh worktree conversation");
});
