import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { expect, type Locator } from "@playwright/test";
import { runCommand } from "./actions";

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

export async function configureFakeAcpMode(
  page: import("@playwright/test").Page,
  home: string,
  mode: string,
) {
  const registryPath = join(home, ".weavie", "acp", "custom.json");
  const registry = JSON.parse(await readFile(registryPath, "utf8"));
  registry.agents[0].env.WEAVIE_FAKE_ACP_MODE = mode;
  await writeFile(registryPath, JSON.stringify(registry));
  await runCommand(page, "Manage ACP Agents…");
  const dialog = page.locator(".acp-registry-dialog");
  await dialog.getByRole("button", { name: "Reload", exact: true }).click();
  await expect(
    page.locator(".toast", { hasText: "ACP agent definitions were reloaded." }),
  ).toBeVisible();
  await dialog.getByRole("button", { name: "Close", exact: true }).click();
}
