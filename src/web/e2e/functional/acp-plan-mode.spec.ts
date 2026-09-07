import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { createSession, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("Plan shortcut changes ACP collaboration without changing permissions", async ({
  page,
  weavie,
}) => {
  const registryPath = join(weavie.home, ".weavie", "acp", "custom.json");
  const registry = JSON.parse(await readFile(registryPath, "utf8"));
  registry.agents[0].env.WEAVIE_FAKE_ACP_MODE = "collaboration-mode";
  await writeFile(registryPath, JSON.stringify(registry));
  await runCommand(page, "Manage ACP Agents");
  const dialog = page.locator(".acp-registry-dialog");
  await dialog.getByRole("button", { name: "Reload", exact: true }).click();
  await expect(
    page.locator(".toast", { hasText: "ACP agent definitions were reloaded." }),
  ).toBeVisible();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
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
