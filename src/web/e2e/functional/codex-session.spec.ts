import { expect, test } from "../harness/fixtures";

// The fresh-incarnation path is the invariant: no unload/reload is allowed between creation, control discovery,
// and the first submitted turn. Both transports exercise the real HostCore and Codex app-server process seam.
test("new Codex session initializes and accepts its first prompt @cross", async ({ page }) => {
  await page.locator(".session-rail-add").click();
  const prompt = page.locator(".session-prompt");
  await prompt.locator(".session-prompt-select").nth(1).selectOption("codex");
  await prompt.getByRole("combobox", { name: "Branch name" }).fill("codex-first-turn");
  await prompt.locator(".session-prompt-btn-primary").click();

  await expect(page.locator('.session-chip.active[title^="codex-first-turn —"]')).toBeVisible();
  const surface = page.locator('[data-surface="structured-agent"]');
  await expect(surface.locator(".agent-status-model")).toContainText("GPT Test (Medium)");
  await expect(surface.locator(".agent-status-segment", { hasText: "Mode" })).toContainText(
    "Default",
  );

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("first turn works");
  await composer.press("Enter");

  await expect(surface.locator(".agent-entry.agent-tone-user")).toContainText("first turn works");
  await expect(surface.locator(".agent-entry.agent-tone-assistant")).toContainText(
    "echo: first turn works",
  );
});
