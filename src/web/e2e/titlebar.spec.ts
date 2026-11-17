import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page } from "@playwright/test";
import { windowControlLabel } from "../src/chrome/window-control-label";
import { test } from "./harness/network-fixtures";
import { CHROME, installShell } from "./harness/window-chrome";
import { type MessageEnvelope, MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

const labels = (actions: WindowControlAction[]): string[] =>
  actions.map((action) => windowControlLabel(action, false));

async function openApp(page: Page, chrome: WeavieWindowChrome | null): Promise<MockHost> {
  if (chrome !== null) await installShell(page, "linux", chrome);
  const host = await MockHost.start({ distDir, sessions: [mockSession("main", "main", "acp")] });
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  return host;
}

const hostEvents = (host: MockHost, name: string, after: number): MessageEnvelope[] =>
  host.received
    .slice(after)
    .filter(
      (message) =>
        message.scope === "host" &&
        message.kind === "event" &&
        message.feature === "window" &&
        message.name === name,
    );

for (const [name, chrome] of Object.entries(CHROME)) {
  test(`draws the ${name} chrome`, async ({ page }) => {
    const host = await openApp(page, chrome);
    try {
      const bar = page.locator(".titlebar");
      await expect(bar).toBeVisible();
      await expect(bar.locator(".tb-menu")).toHaveCount(chrome.menus ? 1 : 0);
      await expect(page.locator(".tb-left > .tb-ctl")).toHaveCount(chrome.controls.start.length);
      const start = await page
        .locator(".tb-left > .tb-ctl")
        .evaluateAll((buttons) => buttons.map((button) => button.getAttribute("aria-label")));
      const end = await page
        .locator(".titlebar .tb-controls > .tb-ctl")
        .evaluateAll((buttons) => buttons.map((button) => button.getAttribute("aria-label")));
      expect(start).toEqual(labels(chrome.controls.start));
      expect(end).toEqual(labels(chrome.controls.end));
      const box = await bar.boundingBox();
      expect(box?.height).toBe(chrome.barHeight ?? 38);
      expect(
        await page.locator(".tb-left").evaluate((element) => getComputedStyle(element).paddingLeft),
      ).toBe(`${8 + chrome.inset.start}px`);
      // The omnibar is centred in the window, whatever each side holds.
      const omnibar = await page.locator(".tb-omnibar").boundingBox();
      const width = await page.evaluate(() => window.innerWidth);
      expect(
        Math.abs((omnibar?.x ?? 0) + (omnibar?.width ?? 0) / 2 - width / 2),
      ).toBeLessThanOrEqual(1);
      await expect(page.locator(".resize-frame")).toHaveCount(chrome.resizeHandles ? 1 : 0);
      await expect(page.locator(".tb-build")).toHaveText("test");
    } finally {
      await host.close();
    }
  });
}

test("without a shell config there is no title bar", async ({ page }) => {
  const host = await openApp(page, null);
  try {
    await expect(page.locator(".app")).toBeVisible();
    await expect(page.locator(".titlebar")).toHaveCount(0);
  } finally {
    await host.close();
  }
});

test("a pushed chrome description redraws the bar", async ({ page }) => {
  const host = await openApp(page, CHROME.linux);
  try {
    await expect(page.locator(".tb-left > .tb-ctl")).toHaveCount(2);

    host.publishHost("window", "chrome", {
      ...CHROME.linux,
      controls: { start: [], end: ["minimize", "close"] },
    });

    await expect(page.locator(".tb-left > .tb-ctl")).toHaveCount(0);
    await expect(page.locator(".titlebar .tb-controls > .tb-ctl")).toHaveCount(2);
  } finally {
    await host.close();
  }
});

test("presses on empty bar space go to the host, controls and the search box do not", async ({
  page,
}) => {
  const host = await openApp(page, CHROME.linux);
  try {
    const badge = page.locator(".tb-build");
    const mark = host.checkpoint();

    await badge.click();
    await badge.click({ button: "middle" });
    // A middle press on the bar is the window's, never the page's autoscroll.
    await expect(page.locator(".middle-click-autoscroll-origin")).toHaveCount(0);
    await badge.dblclick();
    await page.locator(".tb-omnibar").click();
    await page.locator(".titlebar .tb-ctl[aria-label='Close']").click();
    await host.waitForHost("event", "window", "control", mark);

    expect(hostEvents(host, "captionPress", mark).map((message) => message.payload)).toEqual([
      { button: 0, count: 1 },
      { button: 1, count: 1 },
      { button: 0, count: 1 },
      { button: 0, count: 2 },
    ]);
    expect(hostEvents(host, "control", mark).map((message) => message.payload)).toEqual([
      { action: "close" },
    ]);
  } finally {
    await host.close();
  }
});

test("a region caption leaves presses to the web engine", async ({ page }) => {
  const host = await openApp(page, CHROME.windows);
  try {
    const mark = host.checkpoint();

    await page.locator(".tb-build").click();
    await page.getByRole("button", { name: "Minimize" }).click();
    await host.waitForHost("event", "window", "control", mark);

    expect(hostEvents(host, "captionPress", mark)).toEqual([]);
  } finally {
    await host.close();
  }
});

test("the welcome screen draws a caption strip only when its window has no title bar", async ({
  page,
}) => {
  const host = await MockHost.start({ distDir, sessions: [] });
  try {
    for (const chrome of [CHROME.linux, { ...CHROME.browser, menus: false }]) {
      await page.addInitScript((injected) => {
        window.__WEAVIE_WELCOME__ = { recents: [], setupCompleted: true, chrome: injected };
      }, chrome);
      await page.goto(host.pageUrl("/welcome.html"), { waitUntil: "domcontentloaded" });
      await expect(page.locator(".welcome")).toBeVisible();
      if (chrome.caption !== "press") {
        await expect(page.locator(".welcome-bar")).toHaveCount(0);
        continue;
      }
      await expect(page.locator(".welcome-bar > .tb-ctl")).toHaveCount(3);
      const mark = host.checkpoint();
      await page.locator(".welcome-bar-fill").click();
      await host.waitForHost("event", "window", "captionPress", mark);
    }
  } finally {
    await host.close();
  }
});

test("a narrow desktop window keeps its window buttons and caption", async ({ page }) => {
  await page.setViewportSize({ width: 720, height: 800 });
  const host = await openApp(page, CHROME.linux);
  try {
    await expect(page.locator(".tb-left > .tb-ctl")).toHaveCount(2);
    for (const button of await page.locator(".titlebar .tb-ctl").all()) {
      await expect(button).toBeVisible();
    }
    const bar = await page.locator(".titlebar").boundingBox();
    expect(bar?.height).toBe(38);
  } finally {
    await host.close();
  }
});

test("a narrow browser page folds its bar into the compact layout", async ({ page }) => {
  await page.setViewportSize({ width: 720, height: 800 });
  const host = await openApp(page, CHROME.browser);
  try {
    await expect(page.locator(".app")).toBeVisible();
    await expect(page.locator(".tb-left")).toBeHidden();
  } finally {
    await host.close();
  }
});

// Wide windows centre the omnibar; narrower ones keep it usable, then fit it between the sides, and never push
// the bar's ends out of the window.
for (const name of ["linux", "linuxLeft", "windows", "mac"] as const) {
  test(`the ${name} bar fits every window width`, async ({ page }) => {
    const host = await openApp(page, CHROME[name]);
    try {
      for (const width of [500, 720, 800, 1000, 1280]) {
        await page.setViewportSize({ width, height: 800 });
        await expect
          .poll(() =>
            page.locator(".titlebar .tb-ctl, .tb-build").evaluateAll(
              (items, width) =>
                items.every((item) => {
                  const box = item.getBoundingClientRect();
                  return box.left >= 0 && box.right <= width;
                }),
              width,
            ),
          )
          .toBe(true);
        if (width > 760) {
          const omnibar = await page.locator(".tb-omnibar").boundingBox();
          expect(omnibar?.width, `omnibar at ${width}px`).toBeGreaterThanOrEqual(300);
        }
      }
    } finally {
      await host.close();
    }
  });
}
