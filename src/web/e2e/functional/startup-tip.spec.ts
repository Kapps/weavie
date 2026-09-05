import { awaitEditorReady, clickOmnibarRowThroughToast } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test.use({ automaticInference: true, dismissStartupTip: false });

test("startup shows one timed tip in the top-center toast surface @cross", async ({ page }) => {
  const tip = page.locator(".toast", { hasText: "Tip:" });
  await tip.hover();
  await expect(tip).toHaveCount(1);
  await expect(tip).toBeVisible();
  await expect(tip).toHaveClass(/toast-timed/);

  const position = await tip.evaluate((element) => {
    const bounds = element.getBoundingClientRect();
    return {
      center: bounds.left + bounds.width / 2,
      top: bounds.top,
      viewportCenter: window.innerWidth / 2,
      splashPresent: document.getElementById("splash") !== null,
    };
  });
  expect(position.splashPresent).toBe(false);
  expect(Math.abs(position.center - position.viewportCenter)).toBeLessThan(2);
  expect(position.top).toBeLessThan(100);

  await awaitEditorReady(page);
  const input = page.locator(".tb-omnibar-input");
  await input.focus();
  await input.fill("e");
  const rows = page.locator(".tb-omnibar-row");
  await expect(rows.first()).toBeVisible();
  await clickOmnibarRowThroughToast(page, rows, tip);
  await expect(page.locator(".editor-tab")).toHaveCount(1);

  await page.mouse.move(0, 0);
  await expect(tip).toHaveCount(0, { timeout: 7_000 });
});
