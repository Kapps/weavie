import { openFile, pressDocumentStart } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import type { WeavieWindow } from "../harness/weavie-window";

test("clipboard commands target the focused definition peek and retain it in the context menu", async ({
  page,
}) => {
  await page.context().grantPermissions(["clipboard-read", "clipboard-write"]);
  await openFile(page, "hello.ts");
  await page.evaluate(() => {
    const monaco = (window as WeavieWindow).__WEAVIE_MONACO__;
    if (monaco === undefined) throw new Error("monaco handle not available");
    monaco.languages.registerDefinitionProvider("*", {
      provideDefinition: (model) => [
        {
          uri: model.uri,
          range: { startLineNumber: 1, startColumn: 17, endLineNumber: 1, endColumn: 22 },
        },
      ],
    });
  });
  await page
    .locator(".view-line", { hasText: "const message = greet" })
    .click({ position: { x: 4, y: 4 } });
  await page.keyboard.press("Home");
  await page.keyboard.press("Shift+End");
  await page.keyboard.press("Alt+F12");
  const peek = page.locator(".peekview-widget");
  await expect(peek).toBeVisible();
  const line = peek.locator(".view-line").first();
  await line.click({ position: { x: 4, y: 4 } });
  await pressDocumentStart(page);
  await page.keyboard.press("Shift+End");
  await page.keyboard.press("ControlOrMeta+c");
  await expect
    .poll(() => page.evaluate(() => navigator.clipboard.readText()))
    .toBe("export function greet(name: string): string {");
  await page.evaluate(() => navigator.clipboard.writeText("replace this clipboard sentinel"));
  await line.locator("span").last().click({ button: "right" });
  const copy = page.getByRole("menu").getByRole("menuitem", { name: /^Copy/ });
  await copy.focus();
  await copy.press("Enter");
  await expect
    .poll(() => page.evaluate(() => navigator.clipboard.readText()))
    .toBe("export function greet(name: string): string {");
});
