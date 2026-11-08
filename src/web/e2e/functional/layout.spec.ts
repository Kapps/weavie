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

// Fullscreen centers the pane in a column whose edges drag symmetrically; the width survives a reload.
test("fullscreen centers the pane in a draggable column", async ({ page }) => {
  const area = page.locator(".pane-area");
  const column = page.locator(".layout-root");
  await page.locator(".pane-fullscreen").first().click();
  await expect(area).toHaveClass(/fullscreen/);
  const areaBox = await area.boundingBox();
  const initial = await column.boundingBox();
  if (areaBox === null || initial === null) throw new Error("no pane area");
  const leftGap = initial.x - areaBox.x;
  const rightGap = areaBox.x + areaBox.width - (initial.x + initial.width);
  expect(Math.abs(leftGap - rightGap)).toBeLessThanOrEqual(1);

  const edge = await page.locator(".fullscreen-edge.right").boundingBox();
  if (edge === null) throw new Error("no fullscreen edge");
  const y = edge.y + edge.height / 2;
  await page.mouse.move(edge.x + edge.width / 2, y);
  await page.mouse.down();
  await page.mouse.move(edge.x + edge.width / 2 - 100, y, { steps: 5 });
  await page.mouse.up();
  const resized = (await column.boundingBox())?.width ?? 0;
  expect(resized).toBeLessThan(Math.min(initial.width, areaBox.width) - 150);

  await page.reload();
  await page.locator(".pane-fullscreen").first().click();
  await expect.poll(async () => (await column.boundingBox())?.width ?? 0).toBeCloseTo(resized, 0);
  await page.locator(".pane-fullscreen:visible").click();
  await expect(area).not.toHaveClass(/fullscreen/);
});
