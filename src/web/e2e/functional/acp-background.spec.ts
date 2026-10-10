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
