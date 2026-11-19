import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect } from "@playwright/test";
import { test } from "./harness/network-fixtures";
import { MockHost, mockSession, NEW_SESSION_COMMAND } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test("a draft longer than the prompt box scrolls clear of the card's tag and the field below", async ({
  page,
}) => {
  const host = await MockHost.start({ distDir });
  try {
    host.setSessions([mockSession("main", "main", "acp")]);
    host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
    await page.setViewportSize({ width: 1200, height: 800 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishHost("commands", "catalog", { commands: [NEW_SESSION_COMMAND], keybindings: [] });
    await page.locator(".session-rail-add").click();

    const prompt = page.getByRole("textbox", { name: "Prompt for a new session" });
    await page.locator(".session-composer-prompt").click({ position: { x: 40, y: 4 } });
    await expect(prompt).toBeFocused();
    await prompt.fill(
      "Rethink how settings are scoped between the client and a remote. ".repeat(20),
    );
    expect(await prompt.evaluate((el) => el.scrollHeight > el.clientHeight)).toBe(true);

    // The textarea's box is where a scrolled draft is clipped.
    const bottom = (box: { y: number; height: number } | null) =>
      (box?.y ?? 0) + (box?.height ?? 0);
    await prompt.evaluate((el) => {
      el.scrollTop = 30;
    });
    const clip = await prompt.boundingBox();
    const tag = await page.locator("#session-start-title").boundingBox();
    const below = await page.getByRole("combobox", { name: "Branch starting point" }).boundingBox();
    expect(clip?.y).toBeGreaterThan(bottom(tag));
    expect((below?.y ?? 0) - bottom(clip)).toBeGreaterThanOrEqual(12);
  } finally {
    await host.close();
  }
});
