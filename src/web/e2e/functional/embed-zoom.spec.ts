import { openFile, runCommand } from "../harness/actions";
import { expect, test } from "../harness/fixtures";
import { ZOOM_IMAGE_SRC } from "../harness/git-workspace";

// Zoomable preview embeds (embed-zoom.ts + EmbedLightbox): every image / rendered Mermaid diagram in the
// Markdown preview and in a source doc gets a hover magnifier that opens a full-app lightbox; arrows step
// between embeds, Escape/backdrop closes, and the `weavie.editor.zoomEmbed` command ($mod+Shift+z) opens or
// advances it — but DECLINES in a plain Monaco view so the chord falls through (it's redo on some
// platforms). Pure web-side feature, so headless-only.

test("preview embeds zoom into the full-app lightbox (magnifier, arrows, Escape, $mod+Shift+z)", async ({
  page,
}) => {
  // zoom.md is seeded with an image (first) and a mermaid fence (second) — see git-workspace.ts.
  await openFile(page, "zoom.md");
  await page.locator(".editor-preview-toggle").click();

  const preview = page.locator(".editor-preview-body");
  await expect(preview.locator("img")).toBeVisible();
  // Mermaid hydrates async from a code-split chunk; the magnifier is re-installed after it lands.
  await expect(preview.locator(".mermaid-rendered > svg")).toBeVisible({ timeout: 15_000 });
  await expect(preview.locator(".embed-zoom")).toHaveCount(2);

  // Hover reveals the magnifier (it sits at opacity 0 until then) and its tooltip advertises the binding.
  const imgButton = preview.locator("span.embed-zoom .embed-zoom-btn");
  await preview.locator("span.embed-zoom img").hover();
  await expect(imgButton).toHaveCSS("opacity", "0.85");
  await expect(imgButton).toHaveAttribute("title", /^Zoom \(.+\)$/);

  // Click it: the lightbox portals to <body> and covers the ENTIRE viewport — over the terminal pane too.
  await imgButton.click();
  const lightbox = page.locator(".embed-lightbox");
  await expect(lightbox).toBeVisible();
  const box = await lightbox.boundingBox();
  expect(box).toEqual({ x: 0, y: 0, ...page.viewportSize() });
  await expect(lightbox.locator(".embed-lightbox-body img")).toBeVisible();
  // The image sits above the mermaid fence in zoom.md, so it's embed 1 of 2 in document order.
  const count = lightbox.locator(".embed-lightbox-count");
  await expect(count).toHaveText("1 / 2 (←/→)");

  // Arrows step (and wrap) between the embeds; the body swaps to the diagram clone.
  await page.keyboard.press("ArrowRight");
  await expect(count).toHaveText("2 / 2 (←/→)");
  await expect(lightbox.locator(".embed-lightbox-body .mermaid-rendered > svg")).toBeVisible();
  await page.keyboard.press("ArrowRight");
  await expect(count).toHaveText("1 / 2 (←/→)");
  await page.keyboard.press("ArrowLeft");
  await expect(count).toHaveText("2 / 2 (←/→)");

  await page.keyboard.press("Escape");
  await expect(lightbox).toHaveCount(0);

  // Keyboard path: with the preview focused, the chord opens on the first embed, and again advances.
  await page.locator(".editor-preview").click();
  await page.keyboard.press("ControlOrMeta+Shift+z");
  await expect(count).toHaveText("1 / 2 (←/→)");
  await page.keyboard.press("ControlOrMeta+Shift+z");
  await expect(count).toHaveText("2 / 2 (←/→)");

  // Wheel zooms toward the cursor: the transform layer scales, the hint shows the factor. The cursor
  // sits at the horizontal center, so the x-translation stays 0 until the drag below moves it.
  const zoomLayer = lightbox.locator(".embed-lightbox-zoom");
  await expect(zoomLayer).toHaveCSS("transform", "matrix(1, 0, 0, 1, 0, 0)");
  const { width, height } = page.viewportSize()!;
  await page.mouse.move(width / 2, height / 2 - 6); // body sits centered above the hint row
  await page.mouse.wheel(0, -600);
  await expect(count).toContainText("× — drag to pan, 0 resets");
  expect(await zoomLayer.evaluate((el) => getComputedStyle(el).transform)).not.toBe(
    "matrix(1, 0, 0, 1, 0, 0)",
  );

  // Dragging a zoomed embed pans it (the translate components move).
  await page.mouse.down();
  await page.mouse.move(width / 2 + 80, height / 2 + 40);
  await page.mouse.up();
  expect(
    await zoomLayer.evaluate((el) => new DOMMatrix(getComputedStyle(el).transform).e),
  ).not.toBe(0);

  // `0` resets to the fitted view and the hint drops the zoom part.
  await page.keyboard.press("0");
  await expect(zoomLayer).toHaveCSS("transform", "matrix(1, 0, 0, 1, 0, 0)");
  await expect(count).toHaveText("2 / 2 (←/→)");

  // Backdrop click closes; a click on the embed itself must NOT dismiss.
  await lightbox.locator(".embed-lightbox-body").click();
  await expect(lightbox).toBeVisible();
  await lightbox.click({ position: { x: 10, y: 10 } });
  await expect(lightbox).toHaveCount(0);
});

// Flaked 2026-07-14 00:45 UTC on Windows CI (test timeout of 30000ms exceeded while setting up "weavie",
// #splash never cleared): https://github.com/Kapps/weavie/actions/runs/29296154331/job/86970001988
// Not this test's fault — the shared `weavie` boot fixture (harness/fixtures.ts) budgets up to 40s for the
// host to boot, longer than Playwright's 30s default test timeout, so a slow Windows boot can kill any test
// before its body even starts. Fixed at the root in playwright.config.ts (raised the per-test timeout on
// non-Linux runners) rather than marking this one test slow.
test("the zoom chord declines in a plain Monaco view — no lightbox", async ({ page }) => {
  await openFile(page, "hello.ts");
  await page.locator(".monaco-editor .view-lines").first().click();
  await page.keyboard.press("ControlOrMeta+Shift+z");
  // Declined ⇒ nothing to await; a settle keeps a late-open from slipping past the count check.
  await page.waitForTimeout(400);
  await expect(page.locator(".embed-lightbox")).toHaveCount(0);
});

test.describe("source docs", () => {
  test.use({
    notionDoc: {
      title: "Zoomable Doc",
      markdown: `A picture: ![pic](${ZOOM_IMAGE_SRC})\n\n\`\`\`mermaid\ngraph LR\n  A --> B\n\`\`\`\n`,
    },
  });

  test("a source doc's embeds get the magnifier and open the lightbox from the shadow root", async ({
    page,
  }) => {
    await runCommand(page, "Open URL");
    const input = page.locator(".url-prompt-input");
    await expect(input).toBeVisible();
    await input.fill("https://www.notion.so/Zoomable-Doc-1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d");
    await input.press("Enter");

    // Playwright pierces the open shadow root the SourceView renders into.
    const source = page.locator(".editor-source");
    await expect(source.locator("img")).toBeVisible({ timeout: 15_000 });
    await expect(source.locator(".mermaid-rendered > svg")).toBeVisible({ timeout: 15_000 });
    // Node labels must be SVG <text>: HTML-in-<foreignObject> labels get stripped by the SVG-profile
    // sanitize, which once shipped diagrams with every label silently deleted.
    await expect(
      source.locator(".mermaid-rendered svg text", { hasText: "A" }).first(),
    ).toBeVisible();
    await expect(source.locator(".embed-zoom")).toHaveCount(2);

    await source.locator("span.embed-zoom img").hover();
    await source.locator("span.embed-zoom .embed-zoom-btn").click();
    const lightbox = page.locator(".embed-lightbox");
    await expect(lightbox.locator(".embed-lightbox-body img")).toBeVisible();
    await expect(lightbox.locator(".embed-lightbox-count")).toHaveText("1 / 2 (←/→)");
    await page.keyboard.press("Escape");
    await expect(lightbox).toHaveCount(0);
  });
});
