import type { ResolvedKeybinding } from "../../src/commands/types";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

test.use({
  permissions: ["clipboard-read", "clipboard-write"],
  preNavigate: {
    run: async (page) => {
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        server.onMessage((data) => {
          const message = JSON.parse(decodeTestWebSocketMessage(data), (key, value) => {
            if (key !== "keybindings") return value;
            // Keep the host's focus guard, but exercise desktop paste over browser transport.
            return (value as ResolvedKeybinding[]).map((binding) =>
              binding.command === "weavie.agent.paste"
                ? { ...binding, when: binding.when?.replace(" && !browserShell", "") }
                : binding,
            );
          });
          socket.send(encodeTestWebSocketMessage(JSON.stringify(message)));
        });
      });
    },
  },
});

test("desktop paste stays in the focused BTW reply and preserves the main draft", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "btw-paste");
  const composer = surface.locator("[data-agent-composer] textarea");
  await submitAcpDraft(surface, "/btw side question");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: side question");
  await composer.fill("main draft stays here");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  await expect(reply).toBeFocused();
  await reply.fill("replace this selection");
  await reply.selectText();
  const pasted = "Pasted reply\nwith a second line";
  await page.evaluate((text) => navigator.clipboard.writeText(text), pasted);
  await page.keyboard.press("ControlOrMeta+v");
  await expect(reply).toHaveValue(pasted);
  await expect(composer).toHaveValue("main draft stays here");
  await reply.press("Enter");
  await expect(aside).toContainText("echo: Pasted reply");
  await expect(reply).toBeHidden();
  await expect(composer).toHaveValue("main draft stays here");
});
