import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page } from "@playwright/test";
import type { CommandInfo } from "../src/commands/types";
import { test } from "./harness/network-fixtures";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

// Each fenced block in an agent reply carries a copy button that copies the block's exact contents, and the
// Copy Code Block command (unbound by default; bound here as a user would) copies the focused block or, with
// none focused, the newest one.

const acpSession = mockSession("cx", "acp", "acp");
const COPY_COMMAND = "weavie.agent.copyCodeBlock";
const CURL = [
  "curl -X POST http://localhost:8080/events \\",
  '  -H "Content-Type: application/json" \\',
  '  -d \'{"type": "test"}\'',
].join("\n");
const WATCH = "    tail -f events.log\n    grep test events.log";
const MARKDOWN = [
  "Send a test event:",
  "",
  "```sh",
  CURL,
  "```",
  "",
  "Then watch:",
  "",
  "```",
  WATCH,
  "```",
].join("\n");

test.use({ permissions: ["clipboard-read", "clipboard-write"] });

test.describe("agent code block copy", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir, sessions: [acpSession] });
  });

  test.afterEach(async () => {
    await host.close();
  });

  const clipboard = (page: Page): Promise<string> =>
    page.evaluate(() => navigator.clipboard.readText());

  test("copies a block's exact contents from its button or the shortcut", async ({ page }) => {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishHost("commands", "catalog", {
      commands: [
        {
          id: COPY_COMMAND,
          title: "Copy Code Block",
          runsIn: "web",
          owner: "backend",
          executionLane: COPY_COMMAND,
          scope: "session",
          description: "",
          aliases: [],
          showInPalette: true,
          when: "agentFocused",
          keys: ["alt+c"],
        } satisfies CommandInfo,
      ],
      keybindings: [{ key: "alt+c", command: COPY_COMMAND, when: "agentFocused" }],
    });
    host.publishAgentPane(acpSession.address, {
      providerId: "acp",
      type: "item-completed",
      itemId: "m1",
      itemType: "agentMessage",
      status: "completed",
      text: MARKDOWN,
    });

    const blocks = page.locator(".agent-markdown .agent-code-block");
    await expect(blocks).toHaveCount(2);
    const first = blocks.first().locator(".agent-code-copy");
    await blocks.first().hover();
    await expect(first).toHaveCSS("opacity", "1");
    await first.hover();
    await expect(first).toHaveAttribute("title", "Copy code (Alt+C)");
    await first.click();
    await expect.poll(() => clipboard(page)).toBe(CURL);
    await expect(first).toHaveClass(/copied/);
    await expect(first).toHaveAttribute("title", "Copied");
    await expect(first).not.toHaveClass(/copied/);

    await page.locator("[data-agent-composer] textarea").focus();
    await page.keyboard.press("Alt+C");
    await expect.poll(() => clipboard(page)).toBe(WATCH);
    await expect(blocks.last().locator(".agent-code-copy")).toHaveClass(/copied/);

    await first.focus();
    await page.keyboard.press("Alt+C");
    await expect.poll(() => clipboard(page)).toBe(CURL);
  });
});
