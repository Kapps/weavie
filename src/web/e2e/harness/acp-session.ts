import { expect, type Locator } from "@playwright/test";

export async function createAcpSession(page: import("@playwright/test").Page, branch: string) {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill(branch);
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();

  await expect(page.locator(`.session-chip.active[title^="${branch} —"]`)).toBeVisible();
  const surface = page.locator('[data-surface="structured-agent"]');
  await expect(surface.locator("[data-agent-composer]")).toHaveAttribute(
    "data-agent-controls-ready",
    "true",
  );
  return surface;
}

export async function submitAcpDraft(surface: Locator, draft: string): Promise<void> {
  const composer = surface.locator("[data-agent-composer]");
  await composer.locator("textarea").fill(draft);
  await expect(composer.locator('button[type="submit"]')).toBeEnabled();
  await composer.locator("textarea").press("Enter");
}
