import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const COMMAND = "dotnet run tools/display-refresh.cs";

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

// Selecting a line of a code block takes the block's boundary newline with it, which pastes into a shell as a
// executed command rather than one sitting on the prompt. Prose keeps its newline.
test("copying a code block line leaves the block's trailing newline behind", async ({ page }) => {
  const session = mockSession("code-copy", "code-copy", "codex");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, {
    generation: 1,
    pageSize: 100,
    messages: [
      {
        providerId: "codex",
        type: "item-completed",
        itemId: "message-0",
        itemType: "agentMessage",
        status: "completed",
        text: `Run this:\n\n\`\`\`bash\n${COMMAND}\n\`\`\`\n\nA paragraph that keeps its own newline.`,
      },
    ],
  });
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();

    const code = page.locator(".agent-markdown pre").first();
    await expect(code).toContainText(COMMAND);
    const clipboard = () => page.evaluate(() => navigator.clipboard.readText());

    await code.click({ clickCount: 3 });
    await page.keyboard.press("ControlOrMeta+c");
    await expect.poll(clipboard).toBe(COMMAND);

    const prose = page.getByText("A paragraph that keeps its own newline.");
    await prose.click({ clickCount: 3 });
    await page.keyboard.press("ControlOrMeta+c");
    await expect.poll(clipboard).toContain("A paragraph that keeps its own newline.");
  } finally {
    await host.close();
  }
});
