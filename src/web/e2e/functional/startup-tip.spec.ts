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
    const paneChrome = Array.from(document.querySelectorAll(".pane-head, .pane-tabs"))
      .map((chrome) => chrome.getBoundingClientRect())
      .filter((chrome) => chrome.width > 0 && chrome.height > 0);
    if (paneChrome.length === 0) {
      throw new Error("visible pane chrome not rendered");
    }
    const top = Math.min(...paneChrome.map((chrome) => chrome.top));
    return {
      center: bounds.left + bounds.width / 2,
      paneChromeBottom: Math.max(
        ...paneChrome
          .filter((chrome) => Math.abs(chrome.top - top) < 1)
          .map((chrome) => chrome.bottom),
      ),
      top: bounds.top,
      viewportCenter: window.innerWidth / 2,
      splashPresent: document.getElementById("splash") !== null,
    };
  });
  expect(position.splashPresent).toBe(false);
  expect(Math.abs(position.center - position.viewportCenter)).toBeLessThan(2);
  expect(position.top).toBeGreaterThanOrEqual(position.paneChromeBottom);
  expect(position.top).toBeLessThan(100);

  await page.mouse.move(0, 0);
  await expect(tip).toHaveCount(0, { timeout: 7_000 });
});
