import {
  allowAutomaticInference,
  awaitEditorReady,
  clickOmnibarRowThroughToast,
} from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ automaticInference: false, dismissInferenceOffer: false });

test("offers and enables automatic inference on desktop", async ({ page }) => {
  expect(page.viewportSize()).toEqual({ width: 1280, height: 800 });
  await awaitEditorReady(page);
  const offer = page.locator(".toast", { hasText: "Let Weavie use automatic inference" });
  await expect(offer).toBeVisible();
  const input = page.locator(".tb-omnibar-input");
  await input.focus();
  await input.fill("e");
  const rows = page.locator(".tb-omnibar-row");
  await expect(rows.first()).toBeVisible();
  await clickOmnibarRowThroughToast(page, rows, offer);
  await expect(page.locator(".editor-tab")).toHaveCount(1);

  await allowAutomaticInference(page);

  await input.focus();
  await input.fill("hello.ts");
  await expect(page.locator(".tb-omnibar-pop")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Shift+N");
  await expect(page.getByRole("dialog", { name: "Sessions" })).toBeVisible();
  await expect(page.locator(".tb-omnibar-pop")).toHaveCount(0);
});
