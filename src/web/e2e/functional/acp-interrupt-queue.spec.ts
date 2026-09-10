import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";

test("interrupt preserves queued slash commands and runs them once in order", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-interrupt-queue");

  await submitAcpDraft(surface, "hold");
  await expect(surface.locator(".agent-working")).toBeVisible();

  for (const command of ["/compact", "/review queued after interrupt"]) {
    await submitAcpDraft(surface, command);
    await expect(surface.locator(".agent-compose-queued")).toContainText(command);
  }
  await surface.getByRole("button", { name: "Interrupt", exact: true }).click();

  const responses = surface.locator(".agent-entry-message.agent-tone-assistant");
  await expect(responses).toHaveCount(3);
  await expect(responses).toContainText([
    "steered: cancelled",
    "Compacting completed.",
    "review command: queued after interrupt",
  ]);
  await expect(surface.locator(".agent-compose-queued")).toHaveCount(0);
  await expect(surface.locator(".agent-working")).toHaveCount(0);

  await submitAcpDraft(surface, "fresh prompt after interrupt");
  await expect(responses).toHaveCount(4);
  await expect(responses).toContainText([
    "steered: cancelled",
    "Compacting completed.",
    "review command: queued after interrupt",
    "echo: fresh prompt after interrupt",
  ]);
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(surface.locator(".agent-entry-notice.agent-tone-error")).toHaveCount(0);
});
