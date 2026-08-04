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

async function swipeAgentPanelLeft(page: import("@playwright/test").Page): Promise<void> {
  const target = page.locator(".agent-surface .agent-body");
  const positions = await target.evaluate((element) => {
    const touch = (identifier: number, clientX: number) =>
      new Touch({ identifier, target: element, clientX, clientY: 240 });
    const move = (clientX: number) =>
      element.dispatchEvent(
        new TouchEvent("touchmove", {
          bubbles: true,
          cancelable: true,
          touches: [touch(1, clientX)],
          targetTouches: [touch(1, clientX)],
        }),
      );
    element.dispatchEvent(
      new TouchEvent("touchstart", {
        bubbles: true,
        cancelable: true,
        touches: [touch(1, 300)],
        targetTouches: [touch(1, 300)],
      }),
    );
    move(220);
    const dragged = element.closest(".pane-area")!.getBoundingClientRect().left;
    move(290);
    const returned = element.closest(".pane-area")!.getBoundingClientRect().left;
    return { dragged, returned };
  });
  expect(positions.returned).toBeGreaterThan(positions.dragged);
  await expect(page.locator(".session-inbox")).toBeVisible();
  await expect(page.locator(".pane-area")).not.toHaveCSS("transform", "none");
  await target.evaluate((element) => {
    const touch = (identifier: number, clientX: number) =>
      new Touch({ identifier, target: element, clientX, clientY: 240 });
    element.dispatchEvent(
      new TouchEvent("touchend", {
        bubbles: true,
        cancelable: true,
        changedTouches: [touch(1, 190)],
      }),
    );
  });
}

test.use({
  fakeScript: {
    steps: [{ op: "hook", request: { hook_event_name: "SessionStart", source: "startup" } }],
  },
  preNavigate: {
    run: (page) =>
      page.addInitScript(() => {
        Object.defineProperty(navigator, "standalone", { configurable: true, value: true });
        document.addEventListener(
          "DOMContentLoaded",
          () => document.documentElement.style.setProperty("--mobile-safe-bottom", "34px"),
          { once: true },
        );
      }),
  },
});

test("compact session inbox creates, resumes, and switches existing surfaces", async ({ page }) => {
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
  expect(geometry.navPaddingBottom).toBe(34);
  expect(geometry.navHeight).toBe(88);
  expect(geometry.paneBottom).toBe(geometry.navTop);

  await newSessionPrompt.fill("Keep this draft");
  await newSessionPrompt.press("Enter");
  await expect(newSessionPrompt).toHaveValue("Keep this draft\n");
  await expect(inbox.locator(".session-inbox-row")).toHaveCount(1);

  await newSessionPrompt.fill("Improve mobile navigation");
  await pasteImage(newSessionPrompt, PNG_B64);
  await expect(inbox.locator(".agent-attachment img")).toBeVisible();
  await inbox.getByRole("combobox", { name: "Agent provider" }).selectOption("codex");
  await expect(inbox.getByRole("button", { name: "Start", exact: true })).toBeEnabled();
  const inboxHistoryLength = await page.evaluate(() => history.length);
  await inbox.getByRole("button", { name: "Start", exact: true }).click();

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
  await agentSurface.getByRole("button", { name: "Run", exact: true }).click();
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
  await agentSurface.evaluate((surface) => {
    const body = surface.querySelector(".agent-body");
    if (!(body instanceof HTMLElement)) {
      throw new Error("Missing agent body");
    }
    const scroller = document.createElement("div");
    scroller.style.cssText = "width:100px;overflow-x:auto";
    scroller.innerHTML = '<div style="width:1000px;height:1px"></div>';
    body.append(scroller);
    const touch = (identifier: number, clientX: number) =>
      new Touch({ identifier, target: scroller, clientX, clientY: 240 });
    scroller.dispatchEvent(
      new TouchEvent("touchstart", {
        bubbles: true,
        touches: [touch(1, 300)],
        targetTouches: [touch(1, 300)],
      }),
    );
    scroller.dispatchEvent(
      new TouchEvent("touchmove", {
        bubbles: true,
        touches: [touch(1, 190)],
        targetTouches: [touch(1, 190)],
      }),
    );
    scroller.dispatchEvent(
      new TouchEvent("touchend", { bubbles: true, changedTouches: [touch(1, 190)] }),
    );
    scroller.remove();

    const option = document.createElement("div");
    option.role = "option";
    option.tabIndex = 0;
    body.append(option);
    const optionTouch = (identifier: number, clientX: number) =>
      new Touch({ identifier, target: option, clientX, clientY: 240 });
    option.dispatchEvent(
      new TouchEvent("touchstart", {
        bubbles: true,
        touches: [optionTouch(2, 300)],
        targetTouches: [optionTouch(2, 300)],
      }),
    );
    option.dispatchEvent(
      new TouchEvent("touchend", { bubbles: true, changedTouches: [optionTouch(2, 190)] }),
    );
    option.remove();
  });
  await expect(agentSurface).toBeVisible();
  await expect(inbox).toBeHidden();
  await swipeAgentPanelLeft(page);
  await expect(inbox).toBeVisible();

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
  const layout = page.locator(".layout-root");
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
