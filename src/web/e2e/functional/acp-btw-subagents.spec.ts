import { writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { sessionWorktrees } from "../harness/git-workspace";

test("BTW subagents stay accessible independently of BTW collapse and survive reload", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "btw-subagent-result");
  await submitAcpDraft(surface, "/btw subagent-held");
  const aside = surface.locator(".agent-aside");
  const group = surface.locator("[data-agent-subagents]");
  await group.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  const card = group.locator("[data-agent-subagent]");
  await expect(card).toHaveAttribute("data-state", "running");
  await expect(aside.locator("[data-agent-subagent]")).toHaveCount(0);
  await aside.getByRole("button", { name: "Collapse BTW", exact: true }).click();
  await expect(card).toBeVisible();
  const identity = await card.getAttribute("data-agent-subagent");

  await card.getByRole("button", { name: "Explore", exact: true }).click();
  const tab = page.locator(".editor-tab", { hasText: "Subagent · Explore" });
  const transcript = page.locator(".editor-agent-view");
  await expect(tab).toBeVisible();
  await expect(transcript).toContainText("counting");
  await writeFile(join(sessionWorktrees(weavie.workspace)[0]!, "release-subagent"), "");
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(transcript).toContainText("held subagent finished");
  await tab.hover();
  await tab.locator(".editor-tab-close").click();

  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await group.getByRole("button", { name: "Expand Subagents", exact: true }).click();
  await expect(card).toHaveAttribute("data-agent-subagent", identity!);
  await expect(card).toHaveAttribute("data-state", "completed");
  await expect(group.locator("[data-agent-subagent]")).toHaveCount(1);
  await card.getByRole("button", { name: "Explore", exact: true }).click();
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

  test("one roster retains descendants and shows approvals while collapsed", async ({ page }) => {
    const surface = await createAcpSession(page, "btw-subagent-children");
    await submitAcpDraft(surface, "/btw subagent-nested");
    const group = surface.locator("[data-agent-subagents]");
    await group.getByRole("button", { name: "Expand Subagents", exact: true }).click();
    const children = group.locator("[data-agent-subagent]");
    await expect(children).toHaveCount(2);
    await expect(children.nth(0)).toContainText("Planner");
    await expect(children.nth(1)).toContainText("Reader");
    await expect(children.nth(1)).toContainText("via Planner");
    await expect(children.nth(1)).toHaveAttribute("data-state", "completed");
    await children.nth(1).getByRole("button", { name: "Reader", exact: true }).click();
    await expect(page.locator(".editor-agent-view")).toContainText("nested finished");
    await group.getByRole("button", { name: "Collapse Subagents", exact: true }).click();

    await submitAcpDraft(surface, "/btw subagent-permission");
    const child = group.locator("[data-agent-subagent]", { hasText: "Editor" });
    const approval = child.locator(".agent-entry-request");
    await expect(
      group.getByRole("button", { name: "Expand Subagents", exact: true }),
    ).toHaveAttribute("aria-expanded", "false");
    await expect(approval).toContainText("README.md");
    await expect(approval).toBeVisible();
    await expect(surface.locator(".agent-aside .agent-entry-request")).toHaveCount(0);
    await group.getByRole("button", { name: "Expand Subagents", exact: true }).click();
    await expect(children).toHaveCount(3);
    await expect(children.first()).toContainText("Editor");
    await group.getByRole("button", { name: "Collapse Subagents", exact: true }).click();
    await expect(approval).toBeVisible();
    await approval.getByRole("button", { name: "Allow" }).click();
    await expect(approval).toHaveCount(0);
    await group.getByRole("button", { name: "Expand Subagents", exact: true }).click();
    await expect(children).toHaveCount(3);
    await expect(child).toHaveAttribute("data-state", "completed");
    await child.getByRole("button", { name: "Editor", exact: true }).click();
    await expect(page.locator(".editor-agent-view")).toContainText("child permission: allow");
  });
});
