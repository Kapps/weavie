import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

async function signal(workspace: string, name: string): Promise<void> {
  await writeFile(join(sessionWorktrees(workspace)[0]!, name), "");
}

test("a subagent renders as a live card that collapses when it finishes", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-subagent-card");
  await submitAcpDraft(surface, "subagent-held");

  const card = surface.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  await expect(card.locator(".agent-entry-summary").first()).toHaveText("Explore");
  await expect(card).toContainText("counting");
  await expect(card.locator(".agent-entry-toggle")).toHaveAttribute("aria-expanded", "true");
  await submitAcpDraft(surface, "/btw side question");
  await expect(surface.locator(".agent-aside")).toContainText("echo: side question");
  await page.setViewportSize({ width: 1280, height: 1600 });
  await card.scrollIntoViewIfNeeded();
  await surface.screenshot({ path: "../../temp/bg984/subagent-running.png" });

  await signal(weavie.workspace, "release-subagent");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.locator(".agent-entry-toggle")).toHaveAttribute("aria-expanded", "false");

  await card.getByRole("button", { name: "Open" }).click();
  const reader = page.getByRole("dialog");
  await expect(reader).toContainText("held subagent finished");
  await page.screenshot({ path: "../../temp/bg984/subagent-reader.png" });
  await page.keyboard.press("Escape");
  await surface.screenshot({ path: "../../temp/bg984/subagent-done.png" });
  await card.getByRole("button", { name: "Open" }).click();
  await page.keyboard.press("Escape");
  await expect(reader).toBeHidden();
});

test("a background task waits in the tray until Stop ends it with a notice", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-background-task");
  await submitAcpDraft(surface, "task-held");

  const pill = surface.locator(".agent-background-tray [data-background-item]");
  await expect(pill).toHaveAttribute("data-state", "running");
  await expect(pill).toContainText("sleep 30");
  await expect(surface.locator(".agent-background-tray")).toHaveAttribute("role", "toolbar");
  await expect(page.locator(".session-chip.active")).toHaveClass(/status-waiting/);
  await page.setViewportSize({ width: 1280, height: 1600 });
  await surface.screenshot({ path: "../../temp/bg984/task-tray.png" });

  await pill.getByRole("button", { name: "Stop", exact: true }).click();
  await expect(pill).toHaveAttribute("data-state", "stopped");
  await expect(pill.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
  await expect(surface.locator(".agent-entry-notice")).toContainText("Task stopped by user");
});

test("a workflow renders a card, and Show Background Work focuses the tray", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "acp-background-workflow");
  await submitAcpDraft(surface, "workflow-held");

  const card = surface.locator("[data-agent-workflow]");
  await expect(card).toHaveAttribute("data-state", "running");
  await expect(card).toContainText("code-review");
  await expect(card).toContainText("3 tools");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toBeVisible();
  await page.setViewportSize({ width: 1280, height: 1600 });
  await surface.screenshot({ path: "../../temp/bg984/workflow-card.png" });

  await page.keyboard.press("ControlOrMeta+Shift+p");
  await page.locator(".tb-omnibar-input").fill(">Show Background Work");
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Show Background Work$/ }) })
    .click();
  await expect(surface.locator(".agent-background-tray button").first()).toBeFocused();
  await page.keyboard.press("ArrowRight");
  await expect(surface.locator(".agent-background-tray .agent-background-stop")).toBeFocused();

  await signal(weavie.workspace, "release-workflow");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(card.getByRole("button", { name: "Stop", exact: true })).toHaveCount(0);
});

async function runPalette(page: import("@playwright/test").Page, title: string): Promise<void> {
  await page.keyboard.press("ControlOrMeta+Shift+p");
  await page.locator(".tb-omnibar-input").fill(`>${title}`);
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: new RegExp(`^${title}$`) }) })
    .click();
}

test("running work survives a page reload and stopping actions confirm before ending it", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-background-guard");
  await submitAcpDraft(surface, "subagent-held");
  const card = surface.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");

  await page.reload();
  await expect(surface.locator(".agent-background-tray [data-background-item]")).toHaveAttribute(
    "data-state",
    "running",
  );

  await runPalette(page, "Unload Session");
  const dialog = page.getByRole("dialog");
  await expect(dialog).toContainText("Stop background work?");
  await expect(dialog).toContainText("Explore — subagent");
  await dialog.getByRole("button", { name: "Keep session" }).click();
  await expect(dialog).toBeHidden();
  await expect(card).toHaveAttribute("data-state", "running");

  await runPalette(page, "Restart Agent");
  await dialog.getByRole("button", { name: "Close anyway" }).click();
  await expect(card).toHaveAttribute("data-state", "cancelled");
  await expect(surface.locator("[data-agent-composer]")).toHaveAttribute(
    "data-agent-controls-ready",
    "true",
  );
});
