import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import { MockHost } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

// Guards the AgentMarkdown linkify contract for the native (Codex) transcript, against a real browser + the
// mock host: an assistant markdown message that quotes a file path inside inline `code` must render that path
// as a clickable link (an <a> INSIDE the <code>), a path whose filename contains `@` (the Playwright recording
// naming) must match, and a path inside a FENCED code block must stay literal (no <a> in <pre>). Clicking an
// inline-code link must post a `reveal-file` for that path. Regression cover for the fix that stopped excluding
// inline `code` from linkify and widened the path grammar to allow `@`.

const codexChip = {
  id: "cx",
  label: "codex",
  active: true,
  loaded: true,
  primary: true,
  providerId: "codex",
  agentSurface: "structured",
  agentInputProtocol: 2,
  status: "idle",
  hue: 150,
  monogram: "C",
};

const AT_PATH = "src/web/e2e/.recordings/page@883bef3dba4a5a81116faeb690fc011f.webm";
const TSX_PATH = "src/web/src/agent/AgentMarkdown.tsx";

// Inline-code paths (one with `@`) plus a fenced block whose path must stay plain.
const ASSISTANT_MARKDOWN = [
  "Done. The fix lives in two files:",
  "",
  `- \`${TSX_PATH}\` — inline \`code\` paths now linkify (only \`pre\` stays literal).`,
  "- `src/web/src/content-links.ts` — `@` is now a valid path character.",
  "",
  `The recording landed at \`${AT_PATH}\`.`,
  "",
  "Fenced — must stay plain text:",
  "",
  "```ts",
  `const PATH = "${TSX_PATH}";`,
  "```",
].join("\n");

const assistantMessage = () => ({
  type: "agent-pane",
  slot: "cx",
  workspace: "/repo",
  message: {
    providerId: "codex",
    type: "item-completed",
    itemId: "m1",
    itemType: "agentMessage",
    status: "completed",
    text: ASSISTANT_MARKDOWN,
  },
});

test.describe("AgentMarkdown transcript links", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({ distDir });
  });

  test.afterEach(async () => {
    await host.close();
  });

  // Mounts the Codex session and pushes the assistant message, retrying until the markdown renders (App
  // registers its host listener only after mount, so an early push can land before anyone is listening).
  async function mount(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const markdown = page.locator(".agent-markdown");
    await expect(async () => {
      host.pushToWeb({ type: "session-list", sessions: [codexChip] });
      host.pushToWeb(assistantMessage());
      await expect(markdown).toBeVisible({ timeout: 1000 });
    }).toPass({ timeout: 20_000 });
  }

  test("linkifies inline-code paths (incl. @), leaves fenced code plain, and reveals on click", async ({
    page,
  }) => {
    await mount(page);

    // Every inline-code path is an <a> nested inside its <code>.
    const codeAnchors = page.locator(".agent-markdown code a");
    await expect(codeAnchors).toHaveCount(3);
    const texts = await codeAnchors.allInnerTexts();
    expect(texts).toContain(TSX_PATH);
    expect(texts).toContain(AT_PATH); // the `@` path matches the widened grammar
    expect(texts).toContain("src/web/src/content-links.ts");

    // The fenced block stays literal: its path text is present but never wrapped in an <a>.
    await expect(page.locator(".agent-markdown pre a")).toHaveCount(0);
    await expect(page.locator(".agent-markdown pre")).toContainText(TSX_PATH);

    // Clicking an inline-code path posts a reveal-file for exactly that path.
    await page.locator(".agent-markdown code a", { hasText: TSX_PATH }).click();
    const reveal = await host.waitForMessage("reveal-file");
    expect(reveal.path).toBe(TSX_PATH);
    expect(reveal.preview).toBe(true);
  });
});
