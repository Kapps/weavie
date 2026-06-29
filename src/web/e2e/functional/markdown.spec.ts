import { openFile, typeInEditor } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

// Open a markdown file → edit it → toggle preview → the preview renders HTML reflecting the edited content
// (emphasis becomes markup, a new heading appears). The preview reads the editor's reactive content (no
// host round-trip), so headless-only.
test("markdown preview renders edited content as HTML", async ({ page }) => {
  await openFile(page, "README.md");

  const heading = `Marker ${Date.now()}`;
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+End");
  await typeInEditor(page, `\n\n# ${heading}\n`);

  await page.locator(".editor-preview-toggle").click();

  const preview = page.locator(".editor-preview-body");
  await expect(preview).toBeVisible();
  // The seed file's "**world**" became real emphasis markup, not literal asterisks...
  await expect(preview.locator("strong")).toHaveText("world");
  // ...and the heading typed into the editor rendered as an <h1>.
  await expect(preview.locator("h1", { hasText: heading })).toBeVisible();
  // The seed's ```ts fence was syntax-highlighted (hljs token spans survived the sanitize)...
  await expect(preview.locator("pre.hljs .hljs-keyword").first()).toBeVisible();
  // ...and the ```mermaid fence was rendered to an SVG by the async hydrate pass. mermaid is a lazily
  // code-split chunk (fetched + parsed + rendered only on hydrate), so allow well past the 5s default for a
  // loaded CI runner to pull it — Playwright keeps polling, so this still fails if the SVG never appears.
  await expect(preview.locator(".mermaid-rendered svg")).toBeVisible({ timeout: 30_000 });
});
