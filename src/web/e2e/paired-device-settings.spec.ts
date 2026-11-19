import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { expect } from "@playwright/test";
import { submitAcpDraft } from "./harness/acp-session";
import { test } from "./harness/network-fixtures";
import { type LaunchOptions, launchHeadless } from "./harness/weavie-host";
import { launchRunner } from "./harness/weavie-runner";

const options: LaunchOptions = {
  fakeScript: null,
  inference: "disabled",
  automaticInference: false,
  setupCompleted: true,
  markdownView: "source",
  reviewFilesAhead: 2,
  workspaceSeed: null,
};

const settingsFile = (home: string): Promise<string> =>
  readFile(join(home, ".weavie", "settings.toml"), "utf8");

// A page served by one host (its home) working in a session on another: the two-machine shape of a native app
// attached to a runner.
test("a remote session's agent changes a device setting on the page's home", async ({ page }) => {
  const runner = await launchRunner(options);
  const home = await launchHeadless({
    ...options,
    remoteAgents: [{ name: "devbox", url: runner.url, token: runner.token }],
  }).catch(async (error) => {
    await runner.stop();
    throw error;
  });
  try {
    const connect = await page.request.post(home.url, {
      form: { token: home.token },
      maxRedirects: 0,
    });
    expect(connect.status()).toBe(302);
    await page.goto(home.url, { waitUntil: "domcontentloaded" });
    await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });

    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    await inbox.getByRole("combobox", { name: "Location" }).selectOption({ label: "devbox" });
    await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("fake-acp");
    await inbox.getByRole("textbox", { name: "Branch for the new session" }).fill("on-devbox");
    await inbox.getByRole("button", { name: "Start", exact: true }).click();
    await expect(page.locator('.session-chip.active[title^="on-devbox"]')).toBeVisible();
    const surface = page.locator('[data-surface="structured-agent"]');
    await expect(surface.locator("[data-agent-composer]")).toHaveAttribute(
      "data-agent-controls-ready",
      "true",
    );

    await submitAcpDraft(surface, 'mcp:setSetting:{"key":"font.size","value":28}');

    await expect
      .poll(() =>
        surface.evaluate((element) =>
          getComputedStyle(element).getPropertyValue("--editor-font-size"),
        ),
      )
      .toBe("28px");
    expect(await settingsFile(home.home)).toContain("font.size = 28");
    expect(await settingsFile(runner.home)).not.toContain("font.size");
  } finally {
    await page.close();
    await home.stop();
    await runner.stop();
  }
});
