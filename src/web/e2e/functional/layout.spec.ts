import { createAcpSession } from "../harness/acp-session";
import { runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { workflowTool } from "../harness/workflows";

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

// Fullscreen centers the pane in a column whose edges drag symmetrically without taking the pane's focus; the
// width survives a reload, and a drag near the edge snaps to filling, which stays grabbable.
test("fullscreen centers the pane in a draggable column", async ({ page }) => {
  await page.setViewportSize({ width: 1600, height: 900 });
  const area = page.locator(".pane-area");
  const column = page.locator(".layout-root");
  const width = async () => (await column.boundingBox())?.width ?? 0;
  const focusedPane = () =>
    page.evaluate(() => document.activeElement?.closest("[data-kind]")?.getAttribute("data-kind"));
  // Drags the right edge from its center to the x that `to` maps it to.
  const dragRightEdge = async (to: (edgeX: number) => number): Promise<void> => {
    const edge = await page.locator(".fullscreen-edge.right").boundingBox();
    if (edge === null) throw new Error("no fullscreen edge");
    const [x, y] = [edge.x + edge.width / 2, edge.y + edge.height / 2];
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(to(x), y, { steps: 5 });
    await page.mouse.up();
  };

  await page.locator(".pane-fullscreen").first().click();
  await expect(area).toHaveClass(/fullscreen/);
  const areaBox = await area.boundingBox();
  const initial = await column.boundingBox();
  if (areaBox === null || initial === null) throw new Error("no pane area");
  const areaRight = areaBox.x + areaBox.width;
  expect(initial.x + initial.width / 2).toBeCloseTo(800, 0);
  const pane = await focusedPane();
  expect(pane).toBeTruthy();

  await dragRightEdge((x) => x - 100);
  const resized = await width();
  expect(resized).toBeLessThan(Math.min(initial.width, areaBox.width) - 150);
  expect(await focusedPane()).toBe(pane);

  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await page.locator(".pane-fullscreen").first().click();
  await expect.poll(width).toBeCloseTo(resized, 0);

  // 14px short of the pane area's edge snaps to filling, and the filled right edge drags back in.
  await dragRightEdge(() => areaRight - 14);
  await expect.poll(width).toBeCloseTo(areaBox.width, 0);
  await page.setViewportSize({ width: 1400, height: 900 });
  await expect.poll(width).toBeCloseTo(areaBox.width - 200, 0);
  await page.setViewportSize({ width: 1600, height: 900 });
  await expect.poll(width).toBeCloseTo(areaBox.width, 0);
  await dragRightEdge((x) => x - 200);
  await expect.poll(width).toBeLessThan(areaBox.width - 300);
  await page.locator(".pane-fullscreen:visible").click();
  await expect(area).not.toHaveClass(/fullscreen/);
});

test("fullscreen centering changes live through MCP and stays clear of the pinned panel", async ({
  page,
}) => {
  await page.setViewportSize({ width: 2000, height: 900 });
  const surface = await createAcpSession(page, "feature/fullscreen-centering");
  await page.locator(".session-rail").hover();
  await page.getByRole("button", { name: /Pin session panel/ }).click();
  await expect(page.locator(".session-panel")).toBeVisible();
  await surface.locator(".pane-fullscreen").click();

  const column = page.locator(".layout-root");
  const area = page.locator(".pane-area");
  const box = async () => {
    const bounds = await column.boundingBox();
    if (bounds === null) throw new Error("no fullscreen column");
    return bounds;
  };
  const midpoint = async () => {
    const bounds = await box();
    return bounds.x + bounds.width / 2;
  };
  const areaBounds = async () => {
    const bounds = await area.boundingBox();
    if (bounds === null) throw new Error("no pane area");
    return bounds;
  };
  const containerCenter = async () => {
    const bounds = await areaBounds();
    return bounds.x + bounds.width / 2;
  };
  const setCenter = async (value: "window" | "container") => {
    await workflowTool(surface, "setSetting", { key: "editor.fullscreenCenter", value });
  };
  const shrink = async (side: "left" | "right") => {
    const before = await box();
    const handle = await page.locator(`.fullscreen-edge.${side}`).boundingBox();
    if (handle === null) throw new Error("no fullscreen edge");
    const x = handle.x + handle.width / 2;
    const y = handle.y + handle.height / 2;
    const available = await areaBounds();
    expect(x).toBeCloseTo(
      side === "left"
        ? Math.max(available.x + 10, before.x)
        : Math.min(available.x + available.width - 10, before.x + before.width),
      0,
    );
    await page.mouse.move(x, y);
    await page.mouse.down();
    await page.mouse.move(x + (side === "left" ? 40 : -40), y, { steps: 5 });
    await page.mouse.up();
    await expect
      .poll(async () => (await box()).width)
      .toBeCloseTo(before.width - (side === "left" ? 80 : 40), 0);
    if (side === "right") {
      await expect
        .poll(async () => {
          const edge = await page.locator(".fullscreen-edge.right").boundingBox();
          if (edge === null) throw new Error("no fullscreen edge");
          return edge.x + edge.width / 2;
        })
        .toBeCloseTo(x - 40, 0);
    }
  };

  await expect.poll(midpoint).toBeCloseTo(1000, 0);
  await setCenter("container");
  await expect.poll(midpoint).toBeCloseTo(await containerCenter(), 0);
  await setCenter("window");
  await expect.poll(midpoint).toBeCloseTo(1000, 0);

  // When the saved width cannot center on the window, it keeps its width and meets the rail.
  const savedWidth = (await box()).width;
  await page.setViewportSize({ width: 1200, height: 900 });
  await expect.poll(async () => (await box()).width).toBeCloseTo((await areaBounds()).width, 0);
  await page.setViewportSize({ width: 1600, height: 900 });
  await expect.poll(async () => (await box()).x).toBeCloseTo((await areaBounds()).x, 0);
  expect((await box()).width).toBeCloseTo(savedWidth, 0);
  await shrink("left");
  await shrink("right");
  expect((await box()).x).toBeGreaterThanOrEqual((await areaBounds()).x);

  await page.setViewportSize({ width: 2000, height: 900 });
  await expect.poll(midpoint).toBeCloseTo(1000, 0);
  await setCenter("container");
  await page.reload();
  await expect(page.locator("#splash")).toHaveCount(0);
  await page.keyboard.press("Escape");
  await surface.locator(".pane-fullscreen").click();
  await expect.poll(midpoint).toBeCloseTo(await containerCenter(), 0);
  await setCenter("window");
  await page.locator(".session-panel-pin").click();
  await page.locator(".pane-area").hover();
  await expect(page.locator(".session-panel")).toHaveCount(0);
  await expect.poll(midpoint).toBeCloseTo(1000, 0);
});
