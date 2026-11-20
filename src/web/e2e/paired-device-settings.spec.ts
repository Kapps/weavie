import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { expect } from "@playwright/test";
import { submitAcpDraft } from "./harness/acp-session";
import { runCommand } from "./harness/actions";
import { test } from "./harness/network-fixtures";
import { type LaunchOptions, launchHeadless } from "./harness/weavie-host";
import { launchRunner } from "./harness/weavie-runner";

const options: LaunchOptions = {
  fakeScript: null,
  inference: "disabled",
  automaticInference: false,
  setupCompleted: true,
  markdownView: "source",
  reviewScreensAhead: 2,
  workspaceSeed: null,
};

const settingsFile = (home: string): Promise<string> =>
  readFile(join(home, ".weavie", "settings.toml"), "utf8");
const themeOverridesFile = (home: string): Promise<string> =>
  readFile(join(home, ".weavie", "theme-overrides.json"), "utf8").catch(() => "");

// A page served by one host (its home) working in a session on another: the two-machine shape of a native app
// attached to a runner.
test("a remote session's device state lands on the page's home and its machine state on the runner", async ({
  page,
}) => {
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
    await page.emulateMedia({ colorScheme: "light" });
    await page.goto(home.url, { waitUntil: "domcontentloaded" });
    await expect(page.locator("#splash")).toHaveCount(0, { timeout: 40_000 });

    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    await inbox
      .getByRole("combobox", { name: "Session location" })
      .selectOption({ label: "devbox" });
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

    await submitAcpDraft(surface, 'mcp:runCommand:{"id":"weavie.font.increase"}');
    await expect.poll(() => settingsFile(home.home)).toContain("font.size = 29");
    expect(await settingsFile(runner.home)).not.toContain("font.size");

    await submitAcpDraft(
      surface,
      'mcp:setThemeOverride:{"key":"editor.background","value":"#102030"}',
    );
    await expect
      .poll(() =>
        page.evaluate(() =>
          getComputedStyle(document.documentElement).getPropertyValue("--bg").trim(),
        ),
      )
      .toBe("#102030");
    expect(JSON.parse(await themeOverridesFile(home.home)).overrides["weavie-light"]).toEqual([
      { kind: "set", key: "editor.background", value: "#102030" },
    ]);
    expect(await themeOverridesFile(runner.home)).not.toContain("#102030");

    // Suggestions run on the session's machine, so configuring them from its session, by its agent or from the
    // palette, edits the runner.
    await submitAcpDraft(surface, 'mcp:runCommand:{"id":"weavie.inference.configure"}');
    const setup = page.locator(".getting-started-dialog");
    const suggestions = setup.getByRole("switch", { name: /Allow suggestions/ });
    await expect(suggestions).toBeChecked();
    await suggestions.click();
    await expect.poll(() => settingsFile(runner.home)).toContain("inference.enabled = false");
    await page.keyboard.press("Escape");
    await expect(setup).toHaveCount(0);

    await runCommand(page, "Configure Suggestions…");
    await expect(suggestions).not.toBeChecked();
    await suggestions.click();
    await expect.poll(() => settingsFile(runner.home)).toContain("inference.enabled = true");
    expect(await settingsFile(home.home)).not.toContain("inference.enabled");
  } finally {
    await page.close();
    await home.stop();
    await runner.stop();
  }
});
