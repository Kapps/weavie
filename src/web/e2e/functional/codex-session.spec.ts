import { expect, test } from "../harness/fixtures";

// The fresh-incarnation path is the invariant: no unload/reload is allowed between creation, control discovery,
// and the first submitted turn. Both transports exercise the real HostCore and Codex app-server process seam.
test("new Codex session initializes and accepts its first prompt @cross", async ({ page }) => {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("codex");
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("codex-first-turn");
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();

  await expect(page.locator('.session-chip.active[title^="codex-first-turn —"]')).toBeVisible();
  const surface = page.locator('[data-surface="structured-agent"]');
  await expect(surface.locator(".agent-status-model")).toContainText("GPT Test (Medium)");
  await expect(surface.locator(".agent-status-segment", { hasText: "Mode" })).toContainText(
    "Default",
  );

  const usage = surface.locator(".agent-status-usage");
  await expect(usage).toHaveAccessibleName("Context window 20% used");
  await usage.hover();
  const tooltip = page.getByRole("tooltip");
  await expect(tooltip).toContainText("65,000 tokens");
  await expect(tooltip).toContainText("5-hour limit");
  await expect(tooltip).toContainText("Weekly limit");

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("first turn works");
  await composer.press("Enter");

  await expect(surface.locator(".agent-entry.agent-tone-user")).toContainText("first turn works");
  await expect(surface.locator(".agent-entry.agent-tone-assistant")).toContainText(
    "echo: first turn works",
  );
  await expect(usage).toHaveAccessibleName("Context window 50% used");
  await usage.hover();
  await expect(tooltip).toContainText("150,000 tokens");

  await page.setViewportSize({ width: 390, height: 844 });
  await page
    .getByRole("navigation", { name: "Workspace surfaces" })
    .getByRole("button", {
      name: "Agent",
      exact: true,
    })
    .click();
  const compactUsage = surface.locator(".agent-status-line-compact .agent-status-usage");
  await compactUsage.focus();
  await expect(tooltip).toBeVisible();
  const bounds = await tooltip.boundingBox();
  expect(bounds?.x).toBeGreaterThanOrEqual(0);
  expect((bounds?.x ?? 390) + (bounds?.width ?? 0)).toBeLessThanOrEqual(390);
});

test("a typed Codex draft survives a page reload", async ({ page }) => {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("codex");
  await inbox
    .getByRole("textbox", { name: "Branch for the new session" })
    .fill("codex-draft-reload");
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();

  const composer = page.locator('[data-surface="structured-agent"] [data-agent-composer] textarea');
  await composer.fill("finish this long response after the reload");
  await page.reload({ waitUntil: "domcontentloaded" });

  await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });
  await expect(composer).toHaveValue("finish this long response after the reload");
});
