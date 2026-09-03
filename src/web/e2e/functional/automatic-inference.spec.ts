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

  const tab = page.locator(".editor-tab.active");
  const close = tab.getByRole("button", { name: "Close" });
  const geometry = await close.evaluate((button) => {
    const toast = document.querySelector<HTMLElement>(".toast");
    if (toast === null) {
      throw new Error("automatic inference toast not rendered");
    }
    const closeRect = button.getBoundingClientRect();
    const toastRect = toast.getBoundingClientRect();
    const center = {
      x: closeRect.left + closeRect.width / 2,
      y: closeRect.top + closeRect.height / 2,
    };
    const target = document.elementFromPoint(center.x, center.y);
    return {
      closeBottom: closeRect.bottom,
      closeCenterX: center.x,
      closeOwnsHit: target === button || button.contains(target),
      toastLeft: toastRect.left,
      toastRight: toastRect.right,
      toastTop: toastRect.top,
    };
  });
  expect(geometry.closeCenterX).toBeGreaterThan(geometry.toastLeft);
  expect(geometry.closeCenterX).toBeLessThan(geometry.toastRight);
  expect(geometry.toastTop).toBeGreaterThanOrEqual(geometry.closeBottom);
  expect(geometry.closeOwnsHit).toBe(true);
  await close.click();
  await expect(tab).toHaveCount(0);
  await expect(offer).toBeVisible();
  await allowAutomaticInference(page);

  await input.focus();
  await input.fill("hello.ts");
  await expect(page.locator(".tb-omnibar-pop")).toBeVisible();
  await page.keyboard.press("ControlOrMeta+Shift+N");
  await expect(page.getByRole("dialog", { name: "Sessions" })).toBeVisible();
  await expect(page.locator(".tb-omnibar-pop")).toHaveCount(0);
});
