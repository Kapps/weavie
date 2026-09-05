import { activeSessionSlot, createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("native agent restart and deletion keep the host usable", async ({ page }) => {
  const original = await activeSessionSlot(page);
  await createSession(page, { branch: "acp-process-lifecycle", provider: "fake-acp" });
  const surface = page.locator('[data-surface="structured-agent"]');
  const composer = surface.locator("[data-agent-composer] textarea");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await composer.fill("before restart");
  await composer.press("Enter");
  await expect(surface).toContainText("echo: before restart");

  await runCommand(page, "Restart Agent");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await composer.fill("after restart");
  await composer.press("Enter");
  await expect(surface).toContainText("echo: after restart");

  await page.locator(".session-chip.active").click({ button: "right" });
  await page.locator(".context-menu-item.danger", { hasText: "Delete" }).click();
  const dialog = page.locator(".confirm-dialog");
  await expect(dialog).toContainText("fake-session-sequence");
  await dialog.getByRole("button", { name: "Delete untracked files…", exact: true }).click();
  await dialog.getByRole("button", { name: "Confirm delete", exact: true }).click();
  await expect(page.locator(".session-chip")).toHaveCount(1);
  await expect(page.locator(".session-chip.active")).toHaveAttribute("data-session-slot", original);

  await createSession(page, { branch: "acp-after-delete", provider: "fake-acp" });
  await composer.fill("after deletion");
  await composer.press("Enter");
  await expect(surface).toContainText("echo: after deletion");
});
