import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("BTW keeps its subagent card and result through completion, tab navigation, and reload", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "btw-subagent-result");
  await submitAcpDraft(surface, "/btw subagent-held");
  const aside = surface.locator(".agent-aside");
  const card = aside.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  const toggle = card.locator(".agent-entry-toggle");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(card.getByText("counting", { exact: true })).toBeHidden();
  await toggle.click();
  await expect(card.getByText("counting", { exact: true })).toBeVisible();
  await toggle.click();
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await aside.getByRole("button", { name: "Collapse BTW", exact: true }).click();
  await expect(card).toBeHidden();
  await surface.locator("[data-agent-composer] textarea").focus();
  await runCommand(page, "Toggle BTW Conversation");
  await expect(aside.getByRole("button", { name: "Collapse BTW", exact: true })).toBeVisible();
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(surface.locator("[data-agent-subagent]")).toHaveCount(1);
  const identity = await card.getAttribute("data-agent-subagent");

  await card.getByRole("button", { name: "Open", exact: true }).click();
  const tab = page.locator(".editor-tab", { hasText: "Subagent · Explore" });
  const transcript = page.locator(".editor-agent-view");
  await expect(tab).toBeVisible();
  await expect(transcript).toContainText("counting");
  await tab.hover();
  await tab.locator(".editor-tab-close").click();
  await expect(tab).toHaveCount(0);

  const tray = surface.locator(".agent-background-tray [data-background-item]", {
    hasText: "Explore",
  });
  await tray.locator(".agent-status-segment").first().click();
  await expect(tab).toBeVisible();
  await expect(transcript).toContainText("counting");

  await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "release-subagent"), "");
  await aside
    .getByRole("button", { name: /Earlier updates/ })
    .first()
    .click();
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(toggle).toHaveAttribute("aria-expanded", "false");
  await expect(transcript).toContainText("held subagent finished");
  await expect(surface.locator("[data-agent-subagent]")).toHaveCount(1);
  await tab.hover();
  await tab.locator(".editor-tab-close").click();
  await card.getByRole("button", { name: "Expand subagent", exact: false }).click();
  await expect(card).toContainText("held subagent finished");

  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await aside
    .getByRole("button", { name: /Earlier updates/ })
    .first()
    .click();
  await expect(card).toHaveAttribute("data-agent-subagent", identity!);
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(surface.locator("[data-agent-subagent]")).toHaveCount(1);
  await page.keyboard.press("Escape");
  await card.getByRole("button", { name: "Open", exact: true }).click();
  await expect(tab).toBeVisible();
  await expect(transcript).toContainText("held subagent finished");
});

test.describe("BTW child ownership", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "mcp", tool: "setSetting", args: { key: "agent.allowAllPermissions", value: false } },
      ],
    },
  });

  test("descendants and approvals stay in their own BTW", async ({ page }) => {
    const surface = await createAcpSession(page, "btw-subagent-children");
    await submitAcpDraft(surface, "/btw subagent-nested");
    const first = surface.locator(".agent-aside").nth(0);
    await first
      .getByRole("button", { name: /Earlier updates/ })
      .first()
      .click();
    const children = first.locator("[data-agent-subagent]");
    await children.first().locator(".agent-entry-toggle").click();
    await expect(children).toHaveCount(2);
    await expect(children.nth(0)).toContainText("Planner");
    await expect(children.nth(1)).toContainText("Reader");
    await expect(children.nth(1)).toContainText("via Planner");
    await expect(children.nth(1)).toHaveAttribute("data-state", "completed");
    await children.nth(1).getByRole("button", { name: "Open", exact: true }).click();
    await expect(page.locator(".editor-agent-view")).toContainText("nested finished");

    await submitAcpDraft(surface, "/btw subagent-permission");
    const second = surface.locator(".agent-aside").nth(1);
    const child = second.locator("[data-agent-subagent]");
    const approval = child.locator(".agent-entry-request");
    await expect(child.locator(".agent-entry-toggle")).toHaveAttribute("aria-expanded", "false");
    await expect(approval).toContainText("README.md");
    await expect(first.locator(".agent-entry-request")).toHaveCount(0);
    await expect(surface.locator("[data-agent-subagent]")).toHaveCount(3);
    await approval.getByRole("button", { name: "Allow" }).click();
    await second
      .getByRole("button", { name: /Earlier updates/ })
      .first()
      .click();
    await expect(child).toHaveAttribute("data-state", "completed");
    await child.getByRole("button", { name: "Open", exact: true }).click();
    await expect(page.locator(".editor-agent-view")).toContainText("child permission: allow");
  });
});
