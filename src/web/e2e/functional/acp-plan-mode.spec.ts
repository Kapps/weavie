import { configureFakeAcpMode } from "../harness/acp-session";
import { createSession } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("Plan shortcut changes ACP collaboration without changing permissions", async ({
  page,
  weavie,
}) => {
  await configureFakeAcpMode(page, weavie.home, "collaboration-mode");
  await createSession(page, { branch: "acp-plan-mode", provider: "fake-acp" });

  const surface = page.locator('[data-surface="structured-agent"]');
  const permissions = surface.getByRole("button", { name: "Permissions Read Only" });
  const defaultMode = surface.getByRole("button", { name: "Collaboration Mode Default" });
  const planMode = surface.getByRole("button", { name: "Collaboration Mode Plan" });
  await expect(permissions).toBeVisible();
  await expect(permissions).not.toHaveAttribute("title", /Shift\+Tab/);
  await expect(defaultMode).toHaveAttribute("title", /Shift\+Tab/);

  const composer = surface.locator("[data-agent-composer] textarea");
  await composer.click();
  await composer.press("Shift+Tab");
  await expect(planMode).toBeVisible();
  await expect(planMode).toHaveAttribute("title", /Shift\+Tab/);
  await expect(permissions).toBeVisible();

  await composer.press("Shift+Tab");
  await expect(defaultMode).toBeVisible();
  await expect(permissions).toBeVisible();
});
