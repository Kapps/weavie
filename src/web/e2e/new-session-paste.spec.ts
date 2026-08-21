import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { CommandIds, type CommandInfo } from "../src/commands/types";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");
const PNG_B64 =
  "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAXUlEQVR42u3PMQ0AIAwAMJTsnhxkI2I3NxLQsGNfkxroqohRcWYtAQEBAQEBAQEBAQEBAQEBAQEBAQEBAYF2IPcd9SpHCQgICAgICAgICAgICAgICAgICAgICAi0fZNauTzyRETRAAAAAElFTkSuQmCC";

function command(
  info: Pick<CommandInfo, "id" | "title" | "runsIn" | "owner" | "executionLane" | "scope" | "keys">,
): CommandInfo {
  return { ...info, description: "", aliases: [], showInPalette: false };
}

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

test("desktop image paste participates in preview and submit while text remains a fallback", async ({
  page,
}) => {
  const host = await MockHost.start({ distDir });
  try {
    let clipboardImage = { mime: "image/png", dataB64: PNG_B64 };
    let clipboardText = "";
    const branchPreviews: unknown[] = [];
    host.setSessions([mockSession("main", "main", "acp")]);
    host.onHost("request", "git", "branches", (request) => host.respond(request, ["main"]));
    host.onHost("request", "clipboard", "readImage", (request) =>
      host.respond(request, clipboardImage),
    );
    host.onHost("request", "clipboard", "read", (request) =>
      host.respond(request, { text: clipboardText }),
    );
    host.onHost("request", "sessionCreation", "previewBranch", (request) => {
      branchPreviews.push(request.payload);
      host.respond(request, { branch: "bug/image-only-task", error: null });
    });

    await page.setViewportSize({ width: 1200, height: 800 });
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const paste = command({
      id: CommandIds.pasteNewSession,
      title: "Paste Into New Session Prompt",
      runsIn: "web",
      owner: "client",
      executionLane: "weavie.session.input",
      scope: "session",
      keys: ["$mod+v"],
    });
    const submit = command({
      id: CommandIds.submitNewSession,
      title: "Start New Session",
      runsIn: "web",
      owner: "client",
      executionLane: "weavie.session.input",
      scope: "session",
      keys: ["Shift+Enter"],
    });
    const create = command({
      id: CommandIds.newSession,
      title: "New Session",
      runsIn: "core",
      owner: "backend",
      executionLane: "weavie.session.lifecycle",
      scope: "host",
      keys: [],
    });
    host.publishHost("commands", "catalog", {
      commands: [paste, submit, create],
      // The mock uses WebSocket, so this native-path test intentionally omits !browserShell.
      keybindings: [
        {
          key: "$mod+v",
          command: paste.id,
          when: "newSessionPromptFocused",
          activeInModal: true,
        },
        {
          key: "Shift+Enter",
          command: submit.id,
          when: "newSessionPromptFocused",
          activeInModal: true,
        },
      ],
    });

    await page.locator(".session-rail-add").click();
    const inbox = page.locator(".session-inbox");
    const prompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
    await expect(prompt).toBeFocused();
    await page.keyboard.press("ControlOrMeta+V");
    const attachment = inbox.locator(".agent-attachment");
    await expect(attachment.locator("img")).toHaveAttribute(
      "src",
      `data:image/png;base64,${PNG_B64}`,
    );
    const branch = inbox.getByRole("textbox", { name: "Branch for the new session" });
    await expect(branch).toHaveValue("bug/image-only-task");
    expect(branchPreviews[0]).toMatchObject({
      sourceId: "main",
      prompt: "",
      attachments: [{ mime: "image/png", dataB64: PNG_B64 }],
      agentProviderId: "claude",
    });

    await attachment.getByTitle("Remove attachment").click();
    clipboardImage = { mime: "", dataB64: "" };
    clipboardText = "pasted";
    await prompt.fill("keep replace tail");
    await prompt.evaluate((element) => (element as HTMLTextAreaElement).setSelectionRange(5, 12));
    await page.keyboard.press("ControlOrMeta+V");
    await expect(prompt).toHaveValue("keep pasted tail");
    await expect
      .poll(() => prompt.evaluate((element) => (element as HTMLTextAreaElement).selectionStart))
      .toBe(11);

    clipboardImage = { mime: "image/png", dataB64: PNG_B64 };
    clipboardText = "";
    await branch.fill("bug/native-image-paste");
    await prompt.focus();
    const invocation = host.waitForHost("request", "sessions", "invoke");
    await page.keyboard.press("ControlOrMeta+V");
    await page.keyboard.press("Shift+Enter");
    expect((await invocation).payload).toMatchObject({
      id: CommandIds.newSession,
      args: {
        branch: "bug/native-image-paste",
        prompt: "keep pasted tail",
        attachments: [{ mime: "image/png", dataB64: PNG_B64 }],
      },
    });
  } finally {
    await host.close();
  }
});
