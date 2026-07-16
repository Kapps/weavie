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
const ABS_TSX_PATH = `/repo/${TSX_PATH}`;
const ABS_AT_PATH = `/repo/${AT_PATH}`;
const FULLSCREEN_COMMAND = "weavie.pane.toggleFullscreen";

// Inline-code paths (one with `@`) plus a fenced block whose path must stay plain.
const ASSISTANT_MARKDOWN = [
  "Done. The fix lives in two files:",
  "",
  `- \`${TSX_PATH}\` — inline \`code\` paths now linkify (only \`pre\` stays literal).`,
  "- `src/web/src/content-links.ts` — `@` is now a valid path character.",
  "",
  `The recording landed at \`${AT_PATH}\`.`,
  "",
  "Docs: https://example.com/docs",
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
    host = await MockHost.start({
      distDir,
      files: { [ABS_TSX_PATH]: "export const promptFocusProbe = true;\n" },
    });
    host.setMedia("cx", ABS_AT_PATH, Buffer.from("focus probe"));
  });

  test.afterEach(async () => {
    await host.close();
  });

  // Mounts the Codex session and pushes the assistant message after `ready` proves App is listening.
  async function mount(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitForMessage("ready");
    const markdown = page.locator(".agent-markdown");
    host.pushToWeb({ type: "session-list", sessions: [codexChip] });
    host.pushToWeb({
      type: "commands",
      commands: [
        {
          id: FULLSCREEN_COMMAND,
          title: "Toggle Fullscreen Pane",
          runsIn: "web",
          description: "",
          aliases: [],
          showInPalette: true,
          when: "",
          keys: ["alt+shift+enter"],
        },
      ],
      keybindings: [{ key: "alt+shift+enter", command: FULLSCREEN_COMMAND }],
    });
    host.pushToWeb({
      type: "set-editor-session",
      sessionId: "cx",
      session: {
        active: ABS_TSX_PATH,
        open: [{ path: ABS_TSX_PATH, viewState: null, preview: true }],
      },
    });
    host.pushToWeb(assistantMessage());
    await expect(markdown).toBeVisible();
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
    await expect(page.locator(".editor")).toHaveAttribute("data-ready", "true");

    // Clicking an inline-code path posts a reveal-file for exactly that path. The file is already open, which
    // exercises the saved-view-state path rather than the fresh-tab line-placement path. Fullscreen also proves
    // the explicit open selects the destination pane before trying to focus it.
    const composer = page.locator("[data-agent-composer] textarea");
    await composer.fill("Keep typing here");
    await page.keyboard.press("Alt+Shift+Enter");
    await expect(page.locator(".fullscreen-exit")).toBeVisible();
    await page.locator(".agent-markdown code a", { hasText: TSX_PATH }).click();
    const reveal = await host.waitForMessage("reveal-file");
    expect(reveal.path).toBe(TSX_PATH);
    expect(reveal.preview).toBe(true);
    await expect(composer).toBeFocused();

    // The host reply selects a different pane, so that new surface intentionally takes focus from the prompt.
    host.pushToWeb({
      type: "open-file",
      path: ABS_TSX_PATH,
      line: 1,
      preview: true,
    });
    await expect(page.locator(".editor-tab", { hasText: "AgentMarkdown.tsx" })).toBeVisible();
    await expect
      .poll(async () =>
        (await page.locator(".editor").getAttribute("data-active-file"))?.replaceAll("\\", "/"),
      )
      .toBe(ABS_TSX_PATH);
    await expect(composer).not.toBeFocused();
    expect(
      await page.evaluate(
        () => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind") ?? null,
      ),
    ).toBe("editor");
    await expect(page.locator(".editor-surface")).toBeVisible();
    await expect(page.locator(".agent-surface")).toBeHidden();
    await page.locator(".fullscreen-exit").click();

    // An already-mounted media destination also regains focus when its link is opened again.
    host.pushToWeb({ type: "open-file", path: ABS_AT_PATH, line: 1, preview: true });
    const media = page.locator(".editor-media");
    await expect(media).toBeVisible();
    await composer.click();
    await page.locator(".agent-markdown code a", { hasText: AT_PATH }).click();
    await expect
      .poll(() => host.received.filter((message) => message.type === "reveal-file").at(-1)?.path)
      .toBe(AT_PATH);
    await expect(composer).toBeFocused();
    host.pushToWeb({ type: "open-file", path: ABS_AT_PATH, line: 1, preview: true });
    await expect(media).toBeFocused();

    // An external URL still opens, and clicking it reselects the agent prompt because no other app pane won.
    const popupPromise = page.waitForEvent("popup");
    await page.locator(".agent-markdown a", { hasText: "https://example.com/docs" }).click();
    await expect(await popupPromise).toHaveURL("https://example.com/docs");
    await expect(composer).toBeFocused();
    await page.keyboard.type(" after URL");
    await expect(composer).toHaveValue("Keep typing here after URL");
  });
});
