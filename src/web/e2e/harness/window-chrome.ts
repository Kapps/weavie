// The window chrome each host hands the page, and the boot shell config that carries it into a mock-host page.

import type { Page } from "@playwright/test";

export const CHROME = {
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
  // A GNOME layout with every button before the title, beside the menus.
  linuxLeft: {
    menus: true,
    caption: "press",
    resizeHandles: false,
    barHeight: null,
    controls: { start: ["close", "minimize", "maximize-toggle"], end: [] },
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
} satisfies Record<string, WeavieWindowChrome>;

/** Boots the page as a desktop shell would: `window.__WEAVIE_SHELL__` with this chrome, before navigation. */
export async function installShell(
  page: Page,
  platform: string,
  chrome: WeavieWindowChrome,
): Promise<void> {
  await page.addInitScript(
    ({ platform, chrome }) => {
      window.__WEAVIE_SHELL__ = {
        platform,
        chrome,
        workspaceLabel: "workspace",
        recents: [],
        buildNumber: "test",
      };
    },
    { platform, chrome },
  );
}
