import { readdir, readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import type { Page } from "@playwright/test";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { openCommandPalette, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { type HeadlessHost, restartAndReconnect } from "../harness/weavie-host";

async function palette(page: Page, query: string) {
  await openCommandPalette(page);
  await page.locator(".tb-omnibar-input").fill(`>${query}`);
  return page.locator(".tb-omnibar-row .tb-row-leaf");
}

test("palette learns abbreviations and persists choices across host restarts", async ({
  page,
  weavie,
}) => {
  test.slow();
  const initial = await (await palette(page, "font")).first().textContent();
  const rows = await palette(page, "font");
  await rows.filter({ hasText: /^Increase Font Size$/ }).click();
  await expect((await palette(page, "")).first()).toHaveText("Increase Font Size");
  await expect((await palette(page, "font")).first()).toHaveText("Increase Font Size");
  await expect((await palette(page, "Decrease Font Size")).first()).toHaveText(
    "Decrease Font Size",
  );

  await restartAndReconnect(page, weavie as HeadlessHost);
  await expect((await palette(page, "")).first()).toHaveText("Increase Font Size");
  await expect((await palette(page, "font")).first()).toHaveText("Increase Font Size");

  await runCommand(page, "Reset Command Learning");
  await expect(page.locator(".toast", { hasText: "Command learning reset" })).toBeVisible();
  await expect((await palette(page, "font")).first()).toHaveText(initial ?? "");
  await expect((await palette(page, "")).first()).not.toHaveText("Increase Font Size");
});

test("slash completion learns accepted commands but not abandoned Tab completions", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "command-learning");
  const composer = surface.locator("[data-agent-composer] textarea");
  const names = surface.locator(".agent-slash-name");
  await submitAcpDraft(surface, "initialize learning session");
  await expect(surface.locator(".agent-entry-message.agent-tone-assistant")).toContainText(
    "echo: initialize learning session",
  );
  await composer.fill("/");
  await expect(names.first()).toBeVisible();
  const initial = await names.allTextContents();

  await composer.fill("/comp");
  await composer.press("Tab");
  await expect(composer).toHaveValue("/compact");
  await composer.fill("/");
  await expect(names).toHaveText(initial);

  await composer.fill("/comp");
  await composer.press("Enter");
  await expect(
    surface.locator(".agent-entry-message.agent-tone-assistant", {
      hasText: "Compacting completed.",
    }),
  ).toBeVisible();
  await composer.fill("/");
  await expect(names.first()).toHaveText("/compact");

  await composer.fill("");
  await page.reload();
  await expect(surface.locator("[data-agent-composer]")).toHaveAttribute(
    "data-agent-controls-ready",
    "true",
  );
  await composer.fill("/");
  await expect(names.first()).toHaveText("/compact");
  await composer.fill("");
  await runCommand(page, "Reset Command Learning");
  await composer.fill("/");
  await expect(names).toHaveText(initial);
});

test("learning can be reset with every session unloaded", async ({ page }) => {
  await runCommand(page, "Increase Font Size");
  await runCommand(page, "Unload Session");
  await expect(page.locator(".session-chip:not(.unloaded)")).toHaveCount(0);
  await runCommand(page, "Reset Command Learning");
  await expect(page.locator(".toast", { hasText: "Command learning reset" })).toBeVisible();
  await expect((await palette(page, "")).first()).not.toHaveText("Increase Font Size");
});

test("nested slash completion retains both queries and excludes command arguments", async ({
  page,
  weavie,
}) => {
  const surface = await createAcpSession(page, "nested-command-learning");
  const composer = surface.locator("[data-agent-composer] textarea").first();
  await composer.fill("/b");
  await composer.press("Tab");
  await expect(composer).toHaveValue("/btw ");
  await composer.fill("/btw /re");
  await surface
    .locator(".agent-slash-option")
    .filter({
      has: page.locator(".agent-slash-name", { hasText: /^\/review / }),
    })
    .click();
  await expect(composer).toHaveValue("/btw /review ");
  const argument = "private-command-argument-canary";
  await composer.fill(`/btw /review ${argument}`);
  await composer.press("Enter");
  await expect(surface.locator(".agent-aside")).toContainText(`review command: ${argument}`);
  await composer.fill("/re");
  await expect(surface.locator(".agent-slash-name").first()).toHaveText("/review <focus>");

  const stateRoot = join(weavie.home, ".weavie", "workspaces");
  const usageFiles = (
    await Promise.all(
      (
        await readdir(stateRoot)
      ).map(async (workspaceId) => {
        const directory = join(stateRoot, workspaceId);
        return (await readdir(directory)).includes("command-usage.json")
          ? [join(directory, "command-usage.json")]
          : [];
      }),
    )
  ).flat();
  expect(usageFiles).toHaveLength(1);
  const serialized = await readFile(usageFiles[0]!, "utf8");
  const usage = JSON.parse(serialized);
  expect(usage).toEqual(
    expect.arrayContaining([
      expect.objectContaining({ id: "weavie.agent.askAside", query: "b" }),
      expect.objectContaining({ id: JSON.stringify(["fake-acp", "review"]), query: "re" }),
    ]),
  );
  expect(serialized).not.toContain(argument);
});

test.describe("learning preference", () => {
  test.use({
    fakeScript: {
      steps: [
        { op: "waitFile", path: "{{WORKSPACE}}/disable-learning" },
        { op: "mcp", tool: "setSetting", args: { key: "commands.learning", value: false } },
        { op: "waitFile", path: "{{WORKSPACE}}/enable-learning" },
        { op: "mcp", tool: "setSetting", args: { key: "commands.learning", value: true } },
      ],
    },
  });

  test("disabling learning stops both ranking and recording", async ({ page, weavie }) => {
    await runCommand(page, "Increase Font Size");
    await expect((await palette(page, "")).first()).toHaveText("Increase Font Size");
    await writeFile(join(weavie.workspace, "disable-learning"), "");
    await expect((await palette(page, "")).first()).not.toHaveText("Increase Font Size");
    await runCommand(page, "Decrease Font Size");
    await expect((await palette(page, "")).first()).not.toHaveText("Decrease Font Size");
    await writeFile(join(weavie.workspace, "enable-learning"), "");
    await expect((await palette(page, "")).first()).toHaveText("Increase Font Size");
  });
});
