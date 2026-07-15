import { openFile } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("SVG preview renders the edited working copy without executing scripts", async ({ page }) => {
  await openFile(page, "sample.svg");

  const toggle = page.locator(".editor-preview-toggle");
  await expect(toggle).toBeVisible();
  await expect(toggle).toHaveAttribute("title", /^Show preview/);

  await toggle.click();

  const image = page.locator(".editor-preview-svg img");
  await expect(image).toBeVisible();
  await expect(image).toHaveAttribute("src", /^blob:/);
  await expect(image).toHaveJSProperty("naturalWidth", 240);
  await expect(image).toHaveJSProperty("naturalHeight", 120);
  expect(await page.evaluate(() => Reflect.has(window, "__weavieSvgScriptRan"))).toBe(false);
  const beforeEdit = await image.screenshot();

  await toggle.click();
  await page.locator(".monaco-editor .view-line", { hasText: "</svg>" }).click();
  await page.keyboard.press("Home");
  await page.keyboard.insertText('<circle id="edited" cx="30" cy="30" r="20" fill="#e07a7a" />');
  await expect(page.locator(".monaco-editor .view-lines")).toContainText('id="edited"');

  await toggle.click();
  await expect(image).toBeVisible();
  expect(await image.screenshot()).not.toEqual(beforeEdit);
});
