import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Page } from "@playwright/test";
import { test } from "./harness/network-fixtures";
import { type MessageEnvelope, MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

type Chrome = WeavieWindowChrome;

// One description per host the bar serves.
const CHROME: Record<string, Chrome> = {
  windows: {
    menus: true,
    caption: "region",
    resizeHandles: true,
    barHeight: null,
    controls: { start: [], end: ["minimize", "maximize-toggle", "close"] },
    inset: { start: 0, end: 0 },
  },
  linux: {
    menus: true,
    caption: "press",
    resizeHandles: false,
    barHeight: null,
    controls: { start: ["close", "minimize"], end: ["maximize-toggle"] },
    inset: { start: 0, end: 0 },
  },
  mac: {
    menus: false,
    caption: "press",
    resizeHandles: false,
    barHeight: 44,
    controls: { start: [], end: [] },
    inset: { start: 78, end: 0 },
  },
  browser: {
    menus: true,
    caption: "none",
    resizeHandles: false,
    barHeight: null,
    controls: { start: [], end: [] },
    inset: { start: 0, end: 0 },
  },
};

const LABELS: Record<string, string> = {
  minimize: "Minimize",
  "maximize-toggle": "Maximize",
  close: "Close",
};

async function openApp(page: Page, chrome: Chrome | null): Promise<MockHost> {
  if (chrome !== null) {
    await page.addInitScript((injected) => {
      window.__WEAVIE_SHELL__ = {
        platform: "linux",
        chrome: injected,
        workspaceLabel: "titlebar-test",
        recents: [],
        buildNumber: "test",
      };
    }, chrome);
  }
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
      expect(start).toEqual(chrome.controls.start.map((action) => LABELS[action]));
      expect(end).toEqual(chrome.controls.end.map((action) => LABELS[action]));
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
