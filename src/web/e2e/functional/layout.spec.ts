import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";

test("the app document never becomes a scroll surface", async ({ page }) => {
  const styles = await page.evaluate(() =>
    [document.documentElement, document.body, document.querySelector("#root")].map((element) => {
      if (!(element instanceof HTMLElement)) {
        throw new Error("Missing app root");
      }
      const style = getComputedStyle(element);
      return { overflow: style.overflow, overscrollBehavior: style.overscrollBehavior };
    }),
  );
  expect(styles).toEqual([
    { overflow: "hidden", overscrollBehavior: "none" },
    { overflow: "hidden", overscrollBehavior: "none" },
    { overflow: "hidden", overscrollBehavior: "none" },
  ]);

  const documentHeight = await page.evaluate(() => {
    const oversizedPortal = document.createElement("div");
    oversizedPortal.style.height = "200vh";
    document.body.append(oversizedPortal);
    return document.body.scrollHeight;
  });
  expect(documentHeight).toBeGreaterThan(await page.evaluate(() => window.innerHeight));

  await page.mouse.move(1, 1);
  await page.mouse.wheel(0, documentHeight);
  await expect.poll(() => page.evaluate(() => window.scrollY)).toBe(0);
});

// Toggling the fullscreen-pane command collapses the layout to a single visible pane and restores it.
// Non-fullscreen panes are hidden (display:none), so the count of visible pane slots is the observable.
// Pure frontend layout, so headless-only.
test("fullscreen pane toggle hides the other panes and restores them", async ({ page }) => {
  const visibleSlots = () => page.locator(".pane-slot:visible").count();
  const initial = await visibleSlots();
  expect(initial).toBeGreaterThan(1);

  await runCommand(page, "Toggle Fullscreen Pane");
  await expect.poll(visibleSlots, { timeout: 10_000 }).toBe(1);

  await runCommand(page, "Toggle Fullscreen Pane");
  await expect.poll(visibleSlots, { timeout: 10_000 }).toBe(initial);
});
