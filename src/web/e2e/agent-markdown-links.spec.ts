import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

// Guards the AgentMarkdown linkify contract for the native (Codex) transcript, against a real browser + the
// mock host: an assistant markdown message that quotes a file path inside inline `code` must render that path
// as a clickable link (an <a> INSIDE the <code>), a path whose filename contains `@` (the Playwright recording
// naming) must match, and a path inside a FENCED code block must stay literal (no <a> in <pre>). Clicking an
// inline-code link must post a `reveal-file` for that path. Regression cover for the fix that stopped excluding
// inline `code` from linkify and widened the path grammar to allow `@`.

const codexSession = mockSession("cx", "codex", "codex");

const AT_PATH = "src/web/e2e/.recordings/page@883bef3dba4a5a81116faeb690fc011f.webm";
const TSX_PATH = "src/web/src/agent/AgentMarkdown.tsx";
const ABS_TSX_PATH = `/repo/${TSX_PATH}`;
const ABS_AT_PATH = `/repo/${AT_PATH}`;
const FULLSCREEN_COMMAND = "weavie.pane.toggleFullscreen";
const TOGGLE_MERMAID_COMMAND = "weavie.agent.toggleMermaidPreview";
const MERMAID_MARKDOWN = [
  "```mermaid",
  "flowchart LR",
  "  A[Streaming] --> B[Complete]",
  "```",
].join("\n");
const INVALID_MERMAID_MARKDOWN = [
  "```mermaid",
  "flowchart LR",
  "  A[Broken] -- B[Missing arrow]",
  "```",
].join("\n");

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
  providerId: "codex",
  type: "item-completed",
  itemId: "m1",
  itemType: "agentMessage",
  status: "completed",
  text: ASSISTANT_MARKDOWN,
});

test.describe("AgentMarkdown transcript links", () => {
  let host: MockHost;

  test.beforeEach(async () => {
    host = await MockHost.start({
      distDir,
      sessions: [codexSession],
      files: { [ABS_TSX_PATH]: "export const promptFocusProbe = true;\n" },
    });
    host.setMedia(codexSession.address.incarnation, ABS_AT_PATH, Buffer.from("focus probe"));
  });

  test.afterEach(async () => {
    await host.close();
  });

  function publishCommands(mermaidKey: string): void {
    host.publishHost("commands", "catalog", {
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
        {
          id: TOGGLE_MERMAID_COMMAND,
          title: "Toggle Mermaid Preview",
          runsIn: "web",
          description: "",
          aliases: [],
          showInPalette: true,
          when: "agentFocused",
          keys: [mermaidKey],
        },
      ],
      keybindings: [
        { key: "alt+shift+enter", command: FULLSCREEN_COMMAND },
        { key: mermaidKey, command: TOGGLE_MERMAID_COMMAND, when: "agentFocused" },
      ],
    });
  }

  async function connect(page: Page): Promise<void> {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    await page.locator(".session-inbox-row").click();
    publishCommands("alt+m");
    host.publishSession(codexSession.address, "editor", "restore", {
      session: {
        active: ABS_TSX_PATH,
        open: [{ path: ABS_TSX_PATH, viewState: null, preview: true }],
      },
    });
  }

  // Mounts the Codex session and pushes the assistant message after `ready` proves App is listening.
  async function mount(page: Page): Promise<void> {
    await connect(page);
    host.publishSession(codexSession.address, "agent", "pane", assistantMessage());
    await expect(page.locator(".agent-markdown")).toBeVisible();
  }

  test("hydrates a Mermaid fence only after its assistant item completes", async ({ page }) => {
    await connect(page);
    const identity = {
      providerId: "codex",
      threadId: "thread-mermaid",
      turnId: "turn-mermaid",
      itemId: "message-mermaid",
      itemType: "agentMessage",
    };
    host.publishSession(codexSession.address, "agent", "pane", {
      ...identity,
      type: "agent-message-delta",
      status: "inProgress",
      text: MERMAID_MARKDOWN,
    });

    const markdown = page.locator(".agent-markdown");
    await expect(markdown.locator("pre.mermaid-pending")).toContainText("flowchart LR");
    await expect(markdown.locator(".mermaid-rendered")).toHaveCount(0);

    host.publishSession(codexSession.address, "agent", "pane", {
      ...identity,
      type: "item-completed",
      status: "completed",
      text: MERMAID_MARKDOWN,
    });

    await expect(markdown.locator(".mermaid-rendered > svg")).toBeVisible();
    await expect(markdown.locator("pre.mermaid-pending")).toHaveCount(0);

    const toggle = markdown.locator(".agent-mermaid-toggle");
    await expect(toggle).toHaveAttribute("title", "Show Mermaid source (Alt+M)");
    await expect(toggle).toHaveAttribute("aria-label", "Toggle Mermaid preview");
    await expect(toggle).toHaveAttribute("aria-pressed", "true");
    publishCommands("alt+shift+m");
    await toggle.hover();
    await expect(toggle).toHaveAttribute("title", "Show Mermaid source (Alt+Shift+M)");
    await toggle.click();
    await expect(markdown.locator("pre.mermaid-source")).toContainText("A[Streaming]");
    await expect(markdown.locator(".mermaid-rendered")).toBeHidden();
    await expect(toggle).toHaveAttribute("title", "Show Mermaid preview (Alt+Shift+M)");
    await page.keyboard.press("Alt+Shift+M");
    await expect(markdown.locator(".mermaid-rendered > svg")).toBeVisible();
  });

  test("keeps invalid Mermaid as source without leaking Mermaid's error diagram", async ({
    page,
  }) => {
    await connect(page);
    host.publishSession(codexSession.address, "agent", "pane", {
      providerId: "codex",
      type: "item-completed",
      itemId: "invalid-mermaid",
      itemType: "agentMessage",
      status: "completed",
      text: INVALID_MERMAID_MARKDOWN,
    });

    const markdown = page.locator(".agent-markdown");
    const source = markdown.locator("pre.mermaid-source");
    await expect(source).toContainText("A[Broken] -- B[Missing arrow]");
    await expect(markdown.locator(".mermaid-rendered")).toHaveCount(0);
    const toggle = markdown.locator(".agent-mermaid-toggle");
    await expect(toggle).toHaveAttribute("aria-disabled", "true");
    await expect(toggle).toHaveAttribute(
      "title",
      "Preview unavailable: Mermaid diagram has a syntax error",
    );
    await expect(page.locator('body > [id^="dweavie-mermaid-"]')).toHaveCount(0);
    await expect(page.getByText("Syntax error in text", { exact: true })).toHaveCount(0);
  });

  test("does not toggle another diagram when the focused Mermaid block cannot preview", async ({
    page,
  }) => {
    await connect(page);
    host.publishSession(codexSession.address, "agent", "pane", {
      providerId: "codex",
      type: "item-completed",
      itemId: "mixed-mermaid",
      itemType: "agentMessage",
      status: "completed",
      text: `${MERMAID_MARKDOWN}\n\n${INVALID_MERMAID_MARKDOWN}`,
    });

    const blocks = page.locator(".agent-mermaid-block");
    await expect(blocks).toHaveCount(2);
    const valid = blocks.first();
    const invalid = blocks.last();
    await expect(valid.locator(".mermaid-rendered > svg")).toBeVisible();
    await invalid.locator(".agent-mermaid-toggle").focus();
    await page.keyboard.press("Alt+M");
    await expect(valid.locator(".mermaid-rendered > svg")).toBeVisible();
    await expect(valid.locator(".agent-mermaid-toggle")).toHaveAttribute("aria-pressed", "true");
  });

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
    const reveal = await host.waitForSession(codexSession.address, "event", "files", "reveal");
    expect(reveal.payload).toMatchObject({ path: TSX_PATH, preview: true });
    await expect(composer).toBeFocused();

    // The host reply selects a different pane, so that new surface intentionally takes focus from the prompt.
    host.publishSession(codexSession.address, "editor", "openFile", {
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
    host.publishSession(codexSession.address, "editor", "openFile", {
      path: ABS_AT_PATH,
      line: 1,
      preview: true,
    });
    const media = page.locator(".editor-media");
    await expect(media).toBeVisible();
    await composer.click();
    const checkpoint = host.checkpoint();
    await page.locator(".agent-markdown code a", { hasText: AT_PATH }).click();
    const mediaReveal = await host.waitForSession(
      codexSession.address,
      "event",
      "files",
      "reveal",
      checkpoint,
    );
    expect(mediaReveal.payload).toMatchObject({ path: AT_PATH });
    await expect(composer).toBeFocused();
    host.publishSession(codexSession.address, "editor", "openFile", {
      path: ABS_AT_PATH,
      line: 1,
      preview: true,
    });
    await expect(media).toBeFocused();

    // An external URL still opens, and clicking it reselects the agent prompt because no other app pane won.
    const popupPromise = page.waitForEvent("popup");
    await page.locator(".agent-markdown a", { hasText: "https://example.com/docs" }).click();
    await expect(await popupPromise).toHaveURL("https://example.com/docs");
    await expect(composer).toBeFocused();
    await page.keyboard.type(" after URL");
    await expect(composer).toHaveValue("Keep typing here after URL");
  });

  test("routes an accepted transcript file destination to Code on compact screens", async ({
    page,
  }) => {
    await page.setViewportSize({ width: 390, height: 844 });
    await connect(page);
    await page.getByRole("button", { name: "Agent", exact: true }).click();
    host.publishSession(codexSession.address, "agent", "pane", assistantMessage());

    const agent = page.locator(".agent-surface");
    const editor = page.locator(".editor-surface");
    await expect(agent).toBeVisible();
    await agent.evaluate((element) => {
      (window as Window & { __mobileAgent?: Element }).__mobileAgent = element;
    });
    await editor.evaluate((element) => {
      (window as Window & { __mobileEditor?: Element }).__mobileEditor = element;
    });

    await page.locator(".agent-markdown code a", { hasText: TSX_PATH }).click();
    const reveal = await host.waitForSession(codexSession.address, "event", "files", "reveal");
    expect(reveal.payload).toMatchObject({ path: TSX_PATH, preview: true });
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

    host.publishSession(codexSession.address, "editor", "openFile", {
      path: ABS_TSX_PATH,
      line: 1,
      preview: true,
    });
    await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
    await expect(editor).toBeVisible();
    await expect
      .poll(async () =>
        (await page.locator(".editor").getAttribute("data-active-file"))?.replaceAll("\\", "/"),
      )
      .toBe(ABS_TSX_PATH);
    expect(
      await page.evaluate(
        () =>
          (window as Window & { __mobileAgent?: Element }).__mobileAgent ===
            document.querySelector(".agent-surface") &&
          (window as Window & { __mobileEditor?: Element }).__mobileEditor ===
            document.querySelector(".editor-surface"),
      ),
    ).toBe(true);
  });
});
