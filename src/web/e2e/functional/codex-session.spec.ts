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

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("first turn works");
  await composer.press("Enter");

  await expect(surface.locator(".agent-entry.agent-tone-user")).toContainText("first turn works");
  await expect(surface.locator(".agent-entry.agent-tone-assistant")).toContainText(
    "echo: first turn works",
  );
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
