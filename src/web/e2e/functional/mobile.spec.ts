import { expect, test } from "../harness/fixtures";

const PNG_B64 =
  "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAIAAAAlC+aJAAAAXUlEQVR42u3PMQ0AIAwAMJTsnhxkI2I3NxLQsGNfkxroqohRcWYtAQEBAQEBAQEBAQEBAQEBAQEBAQEBAYF2IPcd9SpHCQgICAgICAgICAgICAgICAgICAgICAi0fZNauTzyRETRAAAAAElFTkSuQmCC";

async function pasteImage(target: import("@playwright/test").Locator, b64: string): Promise<void> {
  await target.evaluate((element, encoded) => {
    const binary = atob(encoded);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i += 1) {
      bytes[i] = binary.charCodeAt(i);
    }
    const clipboard = new DataTransfer();
    clipboard.items.add(new File([bytes], "mobile.png", { type: "image/png" }));
    element.dispatchEvent(
      new ClipboardEvent("paste", { clipboardData: clipboard, bubbles: true, cancelable: true }),
    );
  }, b64);
}

async function dispatchPaneTouch(
  target: import("@playwright/test").Locator,
  phase: "touchend" | "touchmove" | "touchstart",
  point: { x: number; y: number },
): Promise<boolean> {
  return target.evaluate(
    (element, touchEvent) => {
      const touch = new Touch({
        identifier: 1,
        target: element,
        clientX: touchEvent.point.x,
        clientY: touchEvent.point.y,
      });
      const position =
        touchEvent.phase === "touchend"
          ? { changedTouches: [touch] }
          : { targetTouches: [touch], touches: [touch] };
      return element.dispatchEvent(
        new TouchEvent(touchEvent.phase, {
          bubbles: true,
          cancelable: true,
          ...position,
        }),
      );
    },
    { phase, point },
  );
}

test.use({
  colorScheme: "light",
  fakeScript: {
    steps: [{ op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } }],
  },
  preNavigate: {
    run: (page) =>
      page.addInitScript(() => {
        Object.defineProperty(navigator, "standalone", { configurable: true, value: true });
      }),
  },
});

test("focusing the recent-files search keeps the app viewport fixed", async ({ page }) => {
  const viewport = await page.locator('meta[name="viewport"]').getAttribute("content");
  expect(viewport).toContain("maximum-scale=1");
  expect(viewport).toContain("user-scalable=no");

  await page.getByRole("button", { name: "Code", exact: true }).click();
  await page.getByRole("button", { name: "Recent", exact: true }).click();

  await expect(page.getByPlaceholder("Search recent files…")).toBeFocused();
});

test("tapping the Claude Code prompt focuses its mobile keyboard input", async ({ page }) => {
  await page.locator(".session-inbox-row").click();

  const terminal = page.locator('.terminal-surface[data-kind="terminal:claude"]');
  const screen = terminal.locator(".xterm-screen");
  await expect(terminal).toBeVisible();
  await expect(screen).toBeVisible();

  const bounds = await screen.boundingBox();
  if (bounds === null) {
    throw new Error("Missing Claude terminal bounds");
  }
  await page.touchscreen.tap(bounds.x + 40, bounds.y + bounds.height - 24);

  await expect
    .poll(() =>
      terminal
        .locator(".xterm-helper-textarea")
        .evaluate((element) => document.activeElement === element),
    )
    .toBe(true);
});

test("WebM video opens inline in the compact editor", async ({ page }) => {
  await page.getByRole("button", { name: "Code", exact: true }).click();
  await page.getByRole("button", { name: "Files" }).click();
  await page.locator(".browser-row", { hasText: "clip.webm" }).click();
  await page.getByTitle("Close (Esc)").click();

  const video = page.locator(".editor-media video");
  await expect(video).toBeVisible();
  await expect(video).toHaveAttribute("src", /\/weavie-media\/clip\.webm\?/);
  await expect(video).toHaveAttribute("playsinline", "");
  await expect(video).toHaveAttribute("controls", "");
  await expect(video).toHaveAttribute("preload", "metadata");
  await expect
    .poll(() => video.evaluate((element) => element.readyState))
    .toBeGreaterThanOrEqual(1);
  await expect(page.locator(".editor-media-notice")).toHaveCount(0);
});

test("compact session inbox creates, resumes, and switches existing surfaces", async ({ page }) => {
  await expect(page.locator("html")).toHaveAttribute("data-theme-type", "light");
  const inbox = page.locator(".session-inbox");
  const newSessionPrompt = inbox.getByRole("textbox", { name: "Prompt for a new session" });
  await expect(inbox).toBeVisible();
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  const geometry = await page.evaluate(() => {
    const bounds = (selector: string): DOMRect => {
      const element = document.querySelector(selector);
      if (!(element instanceof HTMLElement)) {
        throw new Error(`Missing ${selector}`);
      }
      return element.getBoundingClientRect();
    };
    const app = bounds(".app");
    const pane = bounds(".pane-area");
    const nav = bounds(".mobile-surface-bar");
    return {
      appBottom: app.bottom,
      mobileStandalone: document.querySelector(".app")?.classList.contains("mobile-standalone"),
      navHeight: nav.height,
      paneBottom: pane.bottom,
      navBottom: nav.bottom,
      navPaddingBottom: Number.parseFloat(
        getComputedStyle(document.querySelector(".mobile-surface-bar")!).paddingBottom,
      ),
      navTop: nav.top,
      viewportHeight: window.innerHeight,
    };
  });
  expect(geometry.mobileStandalone).toBe(true);
  expect(geometry.appBottom).toBe(geometry.viewportHeight);
  expect(geometry.navBottom).toBe(geometry.viewportHeight);
  expect(geometry.navPaddingBottom).toBe(10);
  expect(geometry.navHeight).toBe(60);
  expect(geometry.paneBottom).toBe(geometry.navTop);

  await newSessionPrompt.fill("Keep this draft");
  await newSessionPrompt.press("Enter");
  await expect(newSessionPrompt).toHaveValue("Keep this draft\n");
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  await newSessionPrompt.fill("Improve mobile navigation");
  await pasteImage(newSessionPrompt, PNG_B64);
  await expect(inbox.locator(".agent-attachment img")).toBeVisible();
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("codex");
  const startButton = inbox.getByRole("button", { name: "Start", exact: true });
  await expect(startButton).toBeEnabled();
  const primaryColors = await semanticButtonColors(page);
  await expect(startButton).toHaveCSS("background-color", primaryColors.background);
  await expect(startButton).toHaveCSS("color", primaryColors.foreground);
  const inboxHistoryLength = await page.evaluate(() => history.length);
  await startButton.click();

  await expect(inbox).toBeHidden();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  const agentSurface = page.locator(".agent-surface");
  await expect(agentSurface).toBeVisible();
  await expect(agentSurface).toContainText("echo: Improve mobile navigation");
  expect(await page.evaluate(() => history.length)).toBe(inboxHistoryLength + 1);

  const activeComposer = agentSurface.locator("[data-agent-composer] textarea");
  await activeComposer.fill("First line");
  await activeComposer.press("Enter");
  await expect(activeComposer).toHaveValue("First line\n");
  await expect(agentSurface).not.toContainText("echo: First line");
  const runButton = agentSurface.getByRole("button", { name: "Run", exact: true });
  await expect(runButton).toHaveCSS("background-color", primaryColors.background);
  await expect(runButton).toHaveCSS("color", primaryColors.foreground);
  await runButton.click();
  await expect(agentSurface).toContainText("echo: First line");

  const compactChrome = await agentSurface.evaluate((surface) => {
    const composer = surface.querySelector("[data-agent-composer]")?.getBoundingClientRect();
    const header = surface.querySelector(".pane-head")?.getBoundingClientRect();
    return {
      composerHeight: composer?.height ?? Number.POSITIVE_INFINITY,
      hasFooterStatus: surface.querySelector(":scope > .agent-status-line") !== null,
      headerHeight: header?.height ?? Number.POSITIVE_INFINITY,
      statusInHeader: surface.querySelector(".pane-head .agent-status-line") !== null,
    };
  });
  expect(compactChrome.composerHeight).toBeLessThanOrEqual(60);
  expect(compactChrome.headerHeight).toBeLessThanOrEqual(44);
  expect(compactChrome.hasFooterStatus).toBe(false);
  expect(compactChrome.statusInHeader).toBe(true);

  await page.evaluate(() => history.back());
  await expect(inbox).toBeVisible();
  await page.evaluate(() => history.forward());
  await expect(agentSurface).toBeVisible();

  await page.getByRole("button", { name: "Shell" }).click();
  await expect(page.locator(".terminal-surface[data-kind='terminal:shell']")).toBeVisible();

  await page.getByRole("button", { name: "Code" }).click();
  await expect(page.locator(".editor-surface")).toBeVisible();
  await page.getByRole("button", { name: "Files" }).click();
  const fileRow = page.locator(".browser-row", { hasText: "hello.ts" });
  const browserClose = page.getByTitle("Close (Esc)");
  const [fileRowBox, browserCloseBox] = await Promise.all([
    fileRow.boundingBox(),
    browserClose.boundingBox(),
  ]);
  expect(fileRowBox?.height).toBeGreaterThanOrEqual(44);
  expect(browserCloseBox?.width).toBeGreaterThanOrEqual(44);
  expect(browserCloseBox?.height).toBeGreaterThanOrEqual(44);
  await fileRow.click();
  await browserClose.click();
  await expect(page.locator(".monaco-editor")).toBeVisible();

  expect(await page.evaluate(() => history.length)).toBe(inboxHistoryLength + 1);
  await page.evaluate(() => history.back());
  await expect(inbox).toBeVisible();
  await page.evaluate(() => history.forward());
  await expect(page.locator(".editor-surface")).toBeVisible();

  await page.getByRole("button", { name: "Agent" }).click();
  const scrollerMoveAccepted = await agentSurface.evaluate((surface) => {
    const body = surface.querySelector(".agent-body");
    if (!(body instanceof HTMLElement)) {
      throw new Error("Missing agent body");
    }
    const scroller = document.createElement("div");
    scroller.style.cssText = "width:100px;overflow-x:auto";
    scroller.innerHTML = '<div style="width:1000px;height:1px"></div>';
    body.append(scroller);
    const touch = (clientX: number) =>
      new Touch({ identifier: 1, target: scroller, clientX, clientY: 240 });
    scroller.dispatchEvent(
      new TouchEvent("touchstart", {
        bubbles: true,
        touches: [touch(90)],
        targetTouches: [touch(90)],
      }),
    );
    const moveAccepted = scroller.dispatchEvent(
      new TouchEvent("touchmove", {
        bubbles: true,
        cancelable: true,
        touches: [touch(270)],
        targetTouches: [touch(270)],
      }),
    );
    scroller.dispatchEvent(
      new TouchEvent("touchend", { bubbles: true, changedTouches: [touch(270)] }),
    );
    scroller.remove();
    return moveAccepted;
  });
  expect(scrollerMoveAccepted).toBe(true);

  const agentBody = agentSurface.locator(".agent-body");
  await dispatchPaneTouch(agentBody, "touchstart", { x: 300, y: 240 });
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 120, y: 240 })).toBe(true);
  await dispatchPaneTouch(agentBody, "touchend", { x: 120, y: 240 });
  await expect(agentSurface).toBeVisible();
  await expect(inbox).toBeHidden();
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
  await expect(page.locator(".pane-area")).toHaveCSS("transform", "none");

  await dispatchPaneTouch(agentBody, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 88, y: 243 })).toBe(true);
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 92, y: 300 })).toBe(true);
  expect(await dispatchPaneTouch(agentBody, "touchmove", { x: 220, y: 310 })).toBe(true);
  await dispatchPaneTouch(agentBody, "touchend", { x: 270, y: 310 });
  await expect(agentSurface).toBeVisible();
  await expect(inbox).toBeHidden();
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await dispatchPaneTouch(activeComposer, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(activeComposer, "touchmove", { x: 220, y: 240 })).toBe(true);
  await dispatchPaneTouch(activeComposer, "touchend", { x: 220, y: 240 });
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await activeComposer.fill("Open ./hello.ts");
  await agentSurface.getByRole("button", { name: "Run", exact: true }).click();
  const agentFileLink = agentSurface.getByRole("link", { name: "./hello.ts", exact: true }).last();
  await expect(agentFileLink).toBeVisible();
  const beforeFileNavigation = await page.evaluate(() => history.length);
  await agentFileLink.click();
  const editorSurface = page.locator(".editor-surface");
  await expect(editorSurface).toBeVisible();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
  expect(await page.evaluate(() => history.length)).toBe(beforeFileNavigation + 1);
  expect(
    await page.evaluate(
      () =>
        (
          history.state as {
            __weavieMobileNavigation?: { stack?: unknown };
          }
        ).__weavieMobileNavigation?.stack,
    ),
  ).toEqual(["inbox", "terminal:claude", "editor"]);

  const editorChrome = editorSurface.locator(".editor-tabs");
  await dispatchPaneTouch(editorChrome, "touchstart", { x: 80, y: 240 });
  expect(await dispatchPaneTouch(editorChrome, "touchmove", { x: 120, y: 240 })).toBe(false);
  await expect(agentSurface).toBeVisible();
  await dispatchPaneTouch(editorChrome, "touchend", { x: 120, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");
  await expect(page.locator(".app.mobile-transition")).toHaveCount(0);

  await page.evaluate(() => history.back());
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await page.reload();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  await page.getByRole("button", { name: "Shell" }).click();
  const shellSurface = page.locator(".terminal-surface[data-kind='terminal:shell']");
  await expect(shellSurface).toBeVisible();
  expect(
    await page.evaluate(
      () =>
        (
          history.state as {
            __weavieMobileNavigation?: { stack?: unknown };
          }
        ).__weavieMobileNavigation?.stack,
    ),
  ).toEqual(["inbox", "terminal:claude", "terminal:shell"]);
  await page.goForward();
  await expect(shellSurface).toBeVisible();

  const shellChrome = shellSurface.locator(".pane-head");
  await dispatchPaneTouch(shellChrome, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(shellChrome, "touchmove", { x: 220, y: 240 });
  await expect(agentSurface).toBeVisible();
  await dispatchPaneTouch(shellChrome, "touchend", { x: 270, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  await agentFileLink.click();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Code");

  await dispatchPaneTouch(editorChrome, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 220, y: 240 });
  await expect(agentSurface).toBeVisible();
  const layout = page.locator(".layout-root");
  await expect(layout).not.toHaveCSS("transform", "none");
  const draggedRight = (await layout.boundingBox())!.x;
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 120, y: 240 });
  expect((await layout.boundingBox())!.x).toBeLessThan(draggedRight);
  await dispatchPaneTouch(editorChrome, "touchmove", { x: 270, y: 240 });
  await dispatchPaneTouch(editorChrome, "touchend", { x: 270, y: 240 });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");
  await expect(agentSurface).toBeVisible();

  await dispatchPaneTouch(agentBody, "touchstart", { x: 80, y: 240 });
  await dispatchPaneTouch(agentBody, "touchmove", { x: 220, y: 240 });
  await expect(inbox).toBeVisible();
  await dispatchPaneTouch(agentBody, "touchend", { x: 270, y: 240 });
  await expect(inbox).toBeVisible();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Sessions");

  await expect(inbox.locator(".session-inbox-row")).toHaveCount(2);
  await expect(inbox).toContainText("improve-mobile-navigation");
  await inbox.locator(".session-inbox-row").first().click();
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Agent");

  const bar = page.locator(".mobile-surface-bar");
  await bar.dispatchEvent("pointerdown", {
    clientX: 300,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await bar.dispatchEvent("pointermove", {
    clientX: 220,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await expect(page.locator(".terminal-surface[data-kind='terminal:shell']")).toBeVisible();
  await expect(layout).not.toHaveCSS("transform", "none");
  const draggedLeft = (await layout.boundingBox())!.x;
  await bar.dispatchEvent("pointermove", {
    clientX: 290,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  expect((await layout.boundingBox())!.x).toBeGreaterThan(draggedLeft);
  await bar.dispatchEvent("pointerup", {
    clientX: 120,
    clientY: 20,
    pointerId: 0,
    pointerType: "touch",
  });
  await expect(page.locator(".mobile-surface-button.active")).toHaveText("Shell");
  await expect(bar).toBeFocused();

  await page.getByRole("button", { name: "Sessions" }).click();
  await inbox.locator(".session-inbox-row", { hasText: "improve-mobile-navigation" }).click();
  await expect(agentSurface).toBeVisible();

  await agentSurface.evaluate((element) => {
    (window as Window & { __breakpointProof?: Element }).__breakpointProof = element;
  });

  const horizontalContainment = await agentSurface.evaluate((surface) => {
    const body = surface.querySelector(".agent-body");
    const pane = surface.closest(".pane-area");
    if (!(body instanceof HTMLElement) || !(pane instanceof HTMLElement)) {
      throw new Error("Missing agent scroll containers");
    }
    const wide = document.createElement("div");
    wide.style.width = "1600px";
    wide.style.height = "1px";
    body.append(wide);
    const result = {
      bodyOverflowX: getComputedStyle(body).overflowX,
      documentContained: document.documentElement.scrollWidth === window.innerWidth,
      paneContained: pane.scrollWidth === pane.clientWidth,
    };
    wide.remove();
    return result;
  });
  expect(horizontalContainment.bodyOverflowX).toBe("hidden");
  expect(horizontalContainment.documentContained).toBe(true);
  expect(horizontalContainment.paneContained).toBe(true);

  await page.setViewportSize({ width: 900, height: 844 });
  await expect(page.locator(".session-rail")).toBeVisible();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "Agent" }).click();
  expect(
    await agentSurface.evaluate(
      (element) =>
        (window as Window & { __breakpointProof?: Element }).__breakpointProof === element,
    ),
  ).toBe(true);
});

async function semanticButtonColors(
  page: import("@playwright/test").Page,
): Promise<{ background: string; foreground: string }> {
  return page.evaluate(() => {
    const probe = document.createElement("span");
    probe.style.background = "var(--weavie-button-background)";
    probe.style.color = "var(--weavie-button-foreground)";
    document.body.append(probe);
    const style = getComputedStyle(probe);
    const colors = { background: style.backgroundColor, foreground: style.color };
    probe.remove();
    return colors;
  });
}
