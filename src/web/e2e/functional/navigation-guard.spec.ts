import { openFile, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Clicking a web link in a rendered surface must NEVER navigate the app away (there is no way back). The
// document-level guard (navigation-guard.ts) intercepts the anchor's default action and routes the URL
// through openUrlExternal — in a browser-hosted shell that is window.open, stubbed here so the assertion is
// deterministic (no popup, no network). Headless-only: the guard is pure web.
test("clicking an external link in the markdown preview never navigates the app", async ({
  page,
}) => {
  await openFile(page, "README.md");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+End");
  await typeInEditor(page, "\n\n[weavie-guard-link](https://example.com/weavie-guard)\n");
  await page.locator(".editor-preview-toggle").click();

  const link = page.locator(".editor-preview-body a", { hasText: "weavie-guard-link" });
  await expect(link).toBeVisible();

  // Capture window.open instead of letting it fire: the guard's external route lands here.
  await page.evaluate(() => {
    const opened: string[] = [];
    (window as Window & { __weavieOpened?: string[] }).__weavieOpened = opened;
    window.open = ((url?: string | URL) => {
      opened.push(String(url));
      return null;
    }) as typeof window.open;
  });

  const appUrl = page.url();
  await link.click();

  await expect
    .poll(() =>
      page.evaluate(() => (window as Window & { __weavieOpened?: string[] }).__weavieOpened),
    )
    .toEqual(["https://example.com/weavie-guard"]);
  expect(page.url()).toBe(appUrl); // the app itself never navigated
  await expect(page.locator(".editor-preview-body")).toBeVisible(); // and is still alive
});
