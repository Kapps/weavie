import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { landscapeMedia, renderedFit, zoomMatrix } from "../harness/media-fixtures";

test.use({ workspaceSeed: landscapeMedia });

// Image tabs zoom toward the cursor, pan when zoomed, reset on `0`, toggle fit ↔ 2× on double-click, and a
// quarter turn refits the image so it fills the pane along the swapped axis.
test("an image tab wheel-zooms, pans, resets, double-click zooms, and rotates to fit", async ({
  page,
}) => {
  await openFile(page, "landscape.png");
  const pane = page.locator(".editor-media");
  await expect(pane.locator("img")).toHaveJSProperty("naturalWidth", 1600);
  await expect(pane.locator(".editor-media-zoom")).toHaveCSS(
    "transform",
    "matrix(1, 0, 0, 1, 0, 0)",
  );
  const box = (await pane.boundingBox())!;
  const center = { x: box.x + box.width / 2, y: box.y + box.height / 2 };

  // Zooming toward an off-center cursor scales up and shifts so that point stays put.
  await page.mouse.move(center.x + 100, center.y);
  await page.mouse.wheel(0, -600);
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeGreaterThan(1);
  await expect(pane).toHaveClass(/\bzoomed\b/);
  await expect(pane.locator(".editor-media-hint")).toContainText("× — drag to pan, 0 resets");
  const zoomed = await zoomMatrix(pane);
  expect(zoomed.e).toBeLessThan(0);

  await page.mouse.down();
  await page.mouse.move(center.x + 180, center.y + 40, { steps: 4 });
  await page.mouse.up();
  const panned = await zoomMatrix(pane);
  expect(panned.a).toBe(zoomed.a);
  expect(panned.e - zoomed.e).toBeCloseTo(80, 0);
  expect(panned.f - zoomed.f).toBeCloseTo(40, 0);

  await expect(pane).toBeFocused();
  await page.keyboard.press("0");
  await expect(pane.locator(".editor-media-zoom")).toHaveCSS(
    "transform",
    "matrix(1, 0, 0, 1, 0, 0)",
  );
  await expect(pane.locator(".editor-media-hint")).toHaveCount(0);
  await page.keyboard.press("+");
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeCloseTo(1.25);
  await page.keyboard.press("-");
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeCloseTo(1);

  await page.mouse.dblclick(center.x, center.y);
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBe(2);
  await page.mouse.dblclick(center.x, center.y);
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBe(1);

  // A quarter turn swaps the axes: the landscape image renders portrait, filling one pane axis.
  const rotateButton = pane.getByRole("button", { name: "Rotate 90°" });
  await expect(rotateButton).toHaveAttribute("title", "Rotate 90°");
  await rotateButton.click();
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeCloseTo(0, 5);
  const fitted = { portrait: true, fillsAnAxis: true, fitsInside: true, natural: false };
  expect(await renderedFit(pane)).toEqual(fitted);

  // The button turns with the media; turning twice restores it, and a double-click never reaches the pane's
  // fit ↔ 2× toggle.
  await rotateButton.click();
  await rotateButton.click();
  await rotateButton.dispatchEvent("dblclick");
  await expect.poll(() => renderedFit(pane)).toEqual(fitted);
  await expect(pane).not.toHaveClass(/\bzoomed\b/);

  // Zoom still works on the rotated view, and `0` resets the zoom but keeps the rotation.
  await page.mouse.move(center.x, center.y);
  await page.mouse.wheel(0, -400);
  await expect.poll(async () => (await renderedFit(pane)).fitsInside).toBe(false);
  await pane.focus();
  await page.keyboard.press("0");
  await expect.poll(() => renderedFit(pane)).toEqual(fitted);

  // The palette command turns it back to the fitted landscape view.
  await runCommand(page, "Rotate Media");
  await expect(pane.locator(".editor-media-zoom")).toHaveCSS(
    "transform",
    "matrix(1, 0, 0, 1, 0, 0)",
  );
  expect((await renderedFit(pane)).portrait).toBe(false);
});

// A small image rotates without enlarging past its natural size.
test("rotating a small image keeps it at its natural size", async ({ page }) => {
  await openFile(page, "pixel.png");
  const pane = page.locator(".editor-media");
  await expect(pane.locator("img")).toHaveJSProperty("naturalWidth", 8);
  await pane.getByRole("button", { name: "Rotate 90°" }).click();
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeCloseTo(0, 5);
  expect(await renderedFit(pane)).toMatchObject({ natural: true, fitsInside: true });
});

// The pane's own playback bar plays, pauses, and seeks at 1×, zoomed, and rotated; fullscreen keeps the rotation.
test("a video tab plays through its own controls, zoomed and rotated, and fullscreen keeps the turn", async ({
  page,
}) => {
  await openFile(page, "landscape.webm");
  const pane = page.locator(".editor-media");
  const video = pane.locator("video");
  await expect.poll(() => video.evaluate((element) => element.readyState)).toBeGreaterThan(0);
  await expect(video).not.toHaveAttribute("controls");
  await video.evaluate((element) => element.pause());
  const paused = () => video.evaluate((element) => element.paused);
  const play = pane.getByRole("button", { name: "Play" });
  const pause = pane.getByRole("button", { name: "Pause" });

  await play.click();
  await expect.poll(paused).toBe(false);
  await pause.click();
  await expect.poll(paused).toBe(true);
  await pane.focus();
  await page.keyboard.press(" ");
  await expect.poll(paused).toBe(false);
  await page.keyboard.press(" ");
  await expect.poll(paused).toBe(true);

  await video.hover();
  await page.mouse.wheel(0, -300);
  await expect(pane).toHaveClass(/\bzoomed\b/);
  const before = await zoomMatrix(pane);
  await play.click();
  await pause.click();
  await expect.poll(paused).toBe(true);
  expect(await zoomMatrix(pane)).toEqual(before);
  await pane.focus();
  await page.keyboard.press("0");

  await pane.getByRole("button", { name: "Rotate 90°" }).click();
  await expect.poll(async () => (await zoomMatrix(pane)).a).toBeCloseTo(0, 5);
  const fit = { portrait: true, fitsInside: true, fillsAnAxis: true, natural: false };
  expect(await renderedFit(pane)).toEqual(fit);
  await expect(pane).not.toHaveClass(/\bfullscreen\b/);

  // The turned seek bar runs top to bottom: a press two-thirds down seeks two-thirds in.
  const seek = (await pane.getByRole("slider", { name: "Seek" }).boundingBox())!;
  expect(seek.height).toBeGreaterThan(seek.width);
  await page.mouse.click(seek.x + seek.width / 2, seek.y + (seek.height * 2) / 3);
  await expect
    .poll(() => video.evaluate((element) => element.currentTime / element.duration))
    .toBeCloseTo(2 / 3, 1);

  // Space on a focused control presses it rather than toggling playback.
  await pane.getByRole("button", { name: "Fullscreen" }).focus();
  await page.keyboard.press(" ");
  expect(await paused()).toBe(true);
  await expect
    .poll(() => page.evaluate(() => document.fullscreenElement?.className))
    .toContain("editor-media");
  await expect.poll(() => renderedFit(pane)).toEqual(fit);
  expect((await zoomMatrix(pane)).a).toBeCloseTo(0, 5);
  await pane.getByRole("button", { name: "Exit fullscreen" }).click();
  await expect.poll(() => page.evaluate(() => document.fullscreenElement)).toBeNull();
  await expect(pane).not.toHaveClass(/\bfullscreen\b/);
  await runCommand(page, "Toggle Media Fullscreen");
  await expect(pane).toHaveClass(/\bfullscreen\b/);
  await pane.focus();
  await page.keyboard.press("Escape");
  await expect(pane).not.toHaveClass(/\bfullscreen\b/);
  await expect.poll(() => page.evaluate(() => document.fullscreenElement)).toBeNull();

  await runCommand(page, "Rotate Media");
  expect((await renderedFit(pane)).portrait).toBe(false);
});
