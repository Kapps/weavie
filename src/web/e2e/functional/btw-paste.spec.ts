import type { Page } from "@playwright/test";
import type { ResolvedKeybinding } from "../../src/commands/types";
import { createAcpSession, submitAcpDraft } from "../harness/acp-session";
import { expect, test } from "../harness/fixtures";
import { pastePng } from "../harness/pasted-image";
import { decodeTestWebSocketMessage, encodeTestWebSocketMessage } from "../harness/websocket-codec";

let nativeClipboard = { text: "", image: { mime: "", dataB64: "" } };

test.use({
  preNavigate: {
    run: async (page) => {
      nativeClipboard = { text: "", image: { mime: "", dataB64: "" } };
      await page.routeWebSocket("**/*", (socket) => {
        const server = socket.connectToServer();
        socket.onMessage((data) => {
          const message = JSON.parse(decodeTestWebSocketMessage(data));
          if (
            message.kind === "request" &&
            message.feature === "clipboard" &&
            (message.name === "readImage" || message.name === "read")
          ) {
            socket.send(
              encodeTestWebSocketMessage(
                JSON.stringify({
                  ...message,
                  kind: "response",
                  payload:
                    message.name === "readImage"
                      ? nativeClipboard.image
                      : { text: nativeClipboard.text },
                }),
              ),
            );
          } else {
            server.send(data);
          }
        });
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
  nativeClipboard.text = pasted;
  await page.keyboard.press("ControlOrMeta+v");
  await expect(reply).toHaveValue(pasted);
  await expect(composer).toHaveValue("main draft stays here");
  await reply.press("Enter");
  await expect(aside).toContainText("echo: Pasted reply");
  await expect(reply).toBeHidden();
  await expect(composer).toHaveValue("main draft stays here");
});

async function coloredPng(page: Page, color: string): Promise<string> {
  return page.evaluate((fill) => {
    const canvas = document.createElement("canvas");
    canvas.width = 64;
    canvas.height = 64;
    const context = canvas.getContext("2d");
    if (context === null) throw new Error("Canvas is unavailable");
    context.fillStyle = fill;
    context.fillRect(0, 0, canvas.width, canvas.height);
    return canvas.toDataURL("image/png").split(",")[1]!;
  }, color);
}

test("desktop image paste previews and sends an image-only BTW reply", async ({ page }) => {
  const surface = await createAcpSession(page, "btw-native-image");
  await submitAcpDraft(surface, "/btw image question");
  const aside = surface.locator(".agent-aside");
  await expect(aside).toContainText("echo: image question");
  const composer = surface.locator("[data-agent-composer]");
  await composer.locator("textarea").fill("main draft stays here");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  const reply = aside.getByRole("textbox", { name: "Reply to BTW" });
  const png = await coloredPng(page, "#e35d37");
  nativeClipboard.image = { mime: "image/png", dataB64: png };
  await reply.press("ControlOrMeta+v");
  await expect(aside.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  await expect(aside.getByAltText("Pasted attachment")).toHaveJSProperty("naturalWidth", 64);
  await expect(composer.locator(".agent-attachment")).toHaveCount(0);
  await expect(reply).toHaveValue("");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(aside.locator(".agent-entry-media")).toHaveAttribute(
    "src",
    `data:image/png;base64,${png}`,
  );
  await expect(reply).toBeHidden();
  await expect(composer.locator("textarea")).toHaveValue("main draft stays here");
  await aside.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(aside.locator(".agent-attachment")).toHaveCount(0);
  await expect(aside.getByRole("button", { name: "Reply", exact: true })).toBeDisabled();
});

test("browser pasted images belong to their BTW reply across cancel, removal, and submission", async ({
  page,
}) => {
  const surface = await createAcpSession(page, "btw-image-isolation");
  await submitAcpDraft(surface, "/btw first question");
  await expect(surface.locator(".agent-aside")).toContainText("echo: first question");
  await submitAcpDraft(surface, "/btw second question");
  const first = surface.locator(".agent-aside").nth(0);
  const second = surface.locator(".agent-aside").nth(1);
  await expect(second).toContainText("echo: second question");
  const composer = surface.locator("[data-agent-composer]");
  await composer.locator("textarea").fill("main draft stays here");
  const mainPng = await coloredPng(page, "#2979ff");
  const firstPng = await coloredPng(page, "#e35d37");
  const secondPng = await coloredPng(page, "#13a66b");
  await pastePng(composer.locator("textarea"), mainPng);
  await expect(composer.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  for (const [aside, png] of [
    [first, firstPng],
    [second, secondPng],
  ] as const) {
    await aside.getByRole("button", { name: "Reply", exact: true }).click();
    await pastePng(aside.getByRole("textbox", { name: "Reply to BTW" }), png);
    await expect(aside.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  }
  await second.getByRole("button", { name: "Cancel", exact: true }).click();
  await second.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(second.getByAltText("Pasted attachment")).toBeVisible();
  await second.getByTitle("Remove attachment").click();
  await expect(second.locator(".agent-attachment")).toHaveCount(0);
  await expect(second.getByRole("button", { name: "Reply", exact: true })).toBeDisabled();
  await pastePng(second.getByRole("textbox", { name: "Reply to BTW" }), secondPng);
  await expect(second.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  await first.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(first.locator(".agent-entry-media")).toHaveAttribute(
    "src",
    `data:image/png;base64,${firstPng}`,
  );
  await expect(first.locator(".agent-attachment")).toHaveCount(0);
  await expect(second.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  await expect(composer.locator(".agent-attachment")).toHaveAttribute("title", "ready");
  await expect(composer.locator("textarea")).toHaveValue("main draft stays here");
  await second.getByRole("textbox", { name: "Reply to BTW" }).fill("image");
  await second.getByRole("button", { name: "Reply", exact: true }).click();
  await expect(second).toContainText("image=True");
  await expect(second.locator(".agent-entry-media")).toHaveAttribute(
    "src",
    `data:image/png;base64,${secondPng}`,
  );
  await expect(composer.locator(".agent-attachment")).toHaveAttribute("title", "ready");
});
