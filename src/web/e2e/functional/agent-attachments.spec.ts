import type { Locator, Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("highlighting reply text attaches it, and the agent receives it with the prompt", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "attach-selection");
  await submitAcpDraft(surface, "alpha bravo charlie");
  const reply = surface.locator(".agent-markdown", { hasText: "echo: alpha bravo charlie" });
  await expect(reply).toBeVisible();

  await highlight(page, reply);

  const chip = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText("echo: alpha bravo charlie");

  await submitAcpDraft(surface, "why?");

  await expect(
    surface.locator(".agent-markdown", {
      hasText: "Attached selection (echo: alpha bravo charlie):",
    }),
  ).toBeVisible();
  await expect(chip).toHaveCount(0);
  // The prompt's own selection row, not the agent's echo of it: only the row has the disclosure.
  const row = surface
    .locator(".agent-entry")
    .filter({ has: page.locator(".agent-request-details") })
    .last();
  await row.getByText("show selection").click();
  await expect(row.locator("pre")).toHaveText("echo: alpha bravo charlie");

  // Highlighting the same text again, after the highlight is gone, is a new selection, so it attaches again.
  await page.evaluate(() => document.getSelection()?.removeAllRanges());
  await reply.first().scrollIntoViewIfNeeded();
  await highlight(page, reply.first());
  await expect(chip).toHaveCount(1);
});

test("with attach-on-select off, highlighting attaches nothing until the command runs", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "attach-selection-command");
  await submitAcpDraft(surface, 'mcp:setSetting:{"key":"agent.attachOnSelect","value":false}');
  await expect(surface.locator(".agent-entry-result")).toContainText("setSetting:");
  await submitAcpDraft(surface, "delta echo foxtrot");
  const reply = surface.locator(".agent-markdown", { hasText: "echo: delta echo foxtrot" });
  await expect(reply).toBeVisible();

  await highlight(page, reply);
  const chip = surface.locator("[data-agent-composer] .agent-attachment-selection");
  await page.waitForTimeout(200);
  await expect(chip).toHaveCount(0);

  await page.locator(".tb-omnibar-input").click();
  await page.locator(".tb-omnibar-input").fill(">Attach Selection to Prompt");
  await page
    .locator(".tb-omnibar-row")
    .filter({ has: page.locator(".tb-row-leaf", { hasText: /^Attach Selection to Prompt$/ }) })
    .click();
  await expect(chip).toHaveCount(1);
  await expect(chip).toContainText("echo: delta echo foxtrot");
});

async function highlight(page: Page, reply: Locator): Promise<void> {
  const box = (await reply.locator("p").first().boundingBox())!;
  await page.mouse.move(box.x + 1, box.y + box.height / 2);
  await page.mouse.down();
  await page.mouse.move(box.x + box.width - 1, box.y + box.height / 2, { steps: 8 });
  await page.mouse.up();
}
