import { expect, test } from "../harness/fixtures";

async function createAcpSession(page: import("@playwright/test").Page, branch: string) {
  await page.locator(".session-rail-add").click();
  const inbox = page.locator(".session-inbox");
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
  await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill(branch);
  await inbox.getByRole("button", { name: "Start", exact: true }).click();
  await expect(inbox).toBeHidden();

  await expect(page.locator(`.session-chip.active[title^="${branch} —"]`)).toBeVisible();
  return page.locator('[data-surface="structured-agent"]');
}

// The fresh-incarnation path is the invariant: no unload/reload is allowed between creation, control discovery,
// and the first submitted turn. Both transports exercise the real HostCore and generic ACP process seam.
test("new ACP session initializes and accepts its first prompt @cross", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-first-turn");
  await expect(surface.getByRole("button", { name: "Model Alpha" })).toBeVisible();
  await expect(surface.getByRole("button", { name: "Mode Default" })).toBeVisible();

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await composer.fill("first turn works");
  await composer.press("Enter");

  await expect(surface.locator(".agent-entry.agent-tone-user")).toContainText("first turn works");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "echo: first turn works",
  );
});

test("ACP controls and rich structured output stay native @cross", async ({ page }) => {
  const surface = await createAcpSession(page, "acp-rich-output");

  await surface.getByRole("button", { name: "Model Alpha" }).click();
  await surface.getByRole("option", { name: "Beta" }).click();
  await expect(surface.getByRole("button", { name: "Model Beta" })).toBeVisible();

  await surface.getByRole("button", { name: "Fast Off" }).click();
  await surface.getByRole("option", { name: "On" }).click();
  await expect(surface.getByRole("button", { name: "Fast On" })).toBeVisible();

  await surface.getByRole("button", { name: "Mode Default" }).click();
  await surface.getByRole("option", { name: "Plan" }).click();
  await expect(surface.getByRole("button", { name: "Mode Plan" })).toBeVisible();

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.fill("rich");
  await composer.press("Enter");

  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "rich response",
  );
  const activity = surface.locator(".agent-entry-activity").last();
  await expect(activity).toContainText("edited 1 file");
  await activity.locator("summary").click();
  await expect(activity.getByRole("button", { name: "Review edit" })).toBeVisible();
  await expect(surface.locator(".agent-working")).toHaveCount(0);
});

test("ACP steering and background completion return the session to idle @cross", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "acp-steering");
  const composer = surface.locator("[data-agent-composer] textarea");

  await composer.fill("hold");
  await composer.press("Enter");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await expect(composer).toHaveAttribute("placeholder", "Steer the running turn…");
  await composer.fill("use the native pane");
  await composer.press("Enter");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-user", { hasText: "Steer" }),
  ).toContainText("use the native pane");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "steered: use the native pane",
  );
  await expect(surface.locator(".agent-working")).toHaveCount(0);

  await composer.fill("background");
  await composer.press("Enter");
  const subagentActivity = surface.locator(".agent-entry-activity").last();
  await expect(subagentActivity).toContainText("1 execute");
  await expect(surface.locator(".agent-working")).toBeVisible();
  await composer.fill("finish-background");
  await composer.press("Enter");
  await expect(surface).toContainText("background finished");
  await expect(surface.locator(".agent-working")).toHaveCount(0);
  await expect(composer).toHaveAttribute(
    "placeholder",
    "Write a prompt — / for commands and skills",
  );
});
