import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { clickIntoEditor, openFile, pressDocumentStart } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ inference: "success", automaticInference: true });

for (const failedConfirmation of [false, true]) {
  test.describe(failedConfirmation ? "failed confirmation" : "successful confirmation", () => {
    test.use({
      preNavigate: {
        async run(page) {
          if (!failedConfirmation) return;
          await page.routeWebSocket("**/*", (socket) => {
            const server = socket.connectToServer();
            socket.onMessage((data) => {
              const message = JSON.parse(data.toString());
              if (message.kind === "response" && message.feature === "revise") {
                message.error = "editor confirmation failed";
                server.send(JSON.stringify(message));
              } else {
                server.send(data);
              }
            });
          });
        },
      },
    });

    test("revise selection settles visibly @cross", async ({ page, weavie }) => {
      const path = join(weavie.workspace, "long.ts");
      const original = await readFile(path, "utf8");
      await openFile(page, "long.ts");
      await clickIntoEditor(page);
      await pressDocumentStart(page);
      for (let i = 0; i < 4; i++) await page.keyboard.press("Shift+ArrowDown");
      await page.keyboard.press("ControlOrMeta+Alt+e");
      const prompt = page.locator(".session-prompt-input");
      await expect(prompt).toBeFocused();
      await prompt.fill("Shorten this comment to one line");
      await prompt.press("Enter");

      if (failedConfirmation) {
        await expect(
          page.getByText("Couldn't revise long.ts: editor confirmation failed."),
        ).toBeVisible();
        expect(await readFile(path, "utf8")).toBe(original);
      } else {
        await expect
          .poll(() => readFile(path, "utf8"))
          .toMatch(/^\/\/ revised by the fake\n\/\/ line 5\n/);
        await expect(page.locator(".monaco-editor .view-lines")).toContainText(
          "// revised by the fake",
        );
      }
      await expect(page.locator(".weavie-revising-pill")).toHaveCount(0);
    });
  });
}
