import type { MessageEnvelope } from "../../src/messaging/message-envelope";
import { clickIntoEditor, openCommandPalette, openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

for (const action of ["dock", "float", "hide"] as const) {
  test.describe(`tool focus after ${action}`, () => {
    let armed = false;
    let release: (() => void) | undefined;
    test.use({
      preNavigate: {
        run: async (page) => {
          armed = false;
          release = undefined;
          await page.routeWebSocket("**/*", (socket) => {
            const server = socket.connectToServer();
            let requestId: string | undefined;
            socket.onMessage((data) => {
              const message = JSON.parse(data.toString()) as MessageEnvelope;
              if (
                armed &&
                message.feature === "layout" &&
                message.name === "tool" &&
                (message.payload as { action: string }).action === action
              ) {
                requestId = message.requestId;
              }
              server.send(data);
            });
            server.onMessage((data) => {
              const message = JSON.parse(data.toString()) as MessageEnvelope;
              if (requestId !== undefined && message.requestId === requestId) {
                release = () => socket.send(data);
                requestId = undefined;
              } else socket.send(data);
            });
          });
        },
      },
    });

    for (const interrupted of [false, true]) {
      test(
        interrupted
          ? "a newer palette keeps focus when the layout reply arrives"
          : "the completed layout change focuses its destination",
        async ({ page }) => {
          await openFile(page, "hello.ts");
          await clickIntoEditor(page);
          await page.keyboard.press("ControlOrMeta+Shift+f");
          const search = page.locator('.tool-panel[data-tool="search"]');
          const input = search.locator(".search-input");
          await expect(input).toBeFocused();
          if (action !== "dock") {
            await search.getByRole("button", { name: /^Stay Open/ }).click();
            await expect(search).not.toHaveClass(/tool-floating/);
            await expect(input).toBeFocused();
          }
          armed = true;
          const button =
            action === "dock" ? /^Stay Open/ : action === "float" ? /^Float/ : /^Close/;
          await search.getByRole("button", { name: button }).click();
          await expect.poll(() => release !== undefined).toBe(true);
          if (interrupted) await openCommandPalette(page);
          if (release === undefined) throw new Error("Layout reply was not captured");
          release();
          await page.evaluate(
            () =>
              new Promise<void>((resolve) =>
                requestAnimationFrame(() => requestAnimationFrame(() => resolve())),
              ),
          );
          if (interrupted) await expect(page.locator(".tb-omnibar-input")).toBeFocused();
          else if (action === "hide") {
            await expect(search).toBeHidden();
            await expect(
              page.getByRole("textbox", { name: "Editor content", exact: true }),
            ).toBeFocused();
          } else await expect(input).toBeFocused();
        },
      );
    }
  });
}
