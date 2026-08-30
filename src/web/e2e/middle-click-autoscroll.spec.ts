import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page, test } from "@playwright/test";
import { MockHost, mockSession } from "./mock-host";

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

// Both tests need the same workspace holding one scrollable transcript. The shell claims macOS — the one
// platform whose engine has no autoscroll of its own — so a re-introduced platform gate fails here.
async function openAutoscrollPane(
  page: Page,
  name: string,
): Promise<{ host: MockHost; body: Locator }> {
  const session = mockSession(name, name, "codex");
  await page.addInitScript(() => {
    window.__WEAVIE_SHELL__ = {
      platform: "mac",
      titleBar: "mac",
      workspaceLabel: "autoscroll-test",
      recents: [],
      buildNumber: "test",
    };
  });
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, {
    generation: 1,
    pageSize: 100,
    messages: Array.from({ length: 80 }, (_, index) => ({
      providerId: "codex",
      type: "item-completed",
      itemId: `message-${index}`,
      itemType: "agentMessage",
      status: "completed",
      text: `Agent transcript entry ${index}\n\n${"Scrollable content. ".repeat(12)}`,
    })),
  });
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  const body = page.locator(".agent-body");
  await expect(body).toBeVisible();
  return { host, body };
}

// The centre of a scrollable surface, where a middle click starts an autoscroll.
async function paneOrigin(target: Locator): Promise<{ x: number; y: number }> {
  const bounds = await target.boundingBox();
  if (bounds === null) {
    throw new Error("autoscroll target has no bounds");
  }
  return { x: bounds.x + bounds.width / 2, y: bounds.y + bounds.height / 2 };
}

test("middle-click autoscrolls the agent transcript and responds live", async ({ page }) => {
  const { host, body } = await openAutoscrollPane(page, "autoscroll");

  try {
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);
    const initialTop = await body.evaluate((element) => element.scrollTop);
    const origin = await paneOrigin(body);

    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/middle-click-autoscrolling/);
    await page.mouse.move(origin.x, origin.y - 100);
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeLessThan(initialTop);
    await page.keyboard.press("Escape");
    await expect(body).not.toHaveClass(/middle-click-autoscrolling/);

    await page.mouse.move(origin.x, origin.y);
    await page.mouse.down({ button: "middle" });
    await page.mouse.move(origin.x, origin.y + 100);
    await page.mouse.up({ button: "middle" });
    await expect(body).not.toHaveClass(/middle-click-autoscrolling/);

    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/middle-click-autoscrolling/);
    host.publishHost("settings", "editorOptions", {
      gitBlame: "off",
      middleClickAutoscroll: false,
    });
    await expect(body).not.toHaveClass(/middle-click-autoscrolling/);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).not.toHaveClass(/middle-click-autoscrolling/);
  } finally {
    await host.close();
  }
});

// A wheel listener on window or document — passive or not — takes the page off WebKit's async-scrolling
// path, so every surface scrolls on the main thread. The autoscroll's must exist only while it is running.
test("the autoscroll registers no global wheel listener unless it is running", async ({ page }) => {
  await page.addInitScript(() => {
    let live = 0;
    (window as unknown as { __wheelListeners: () => number }).__wheelListeners = () => live;
    const add = EventTarget.prototype.addEventListener;
    const remove = EventTarget.prototype.removeEventListener;
    const global = (target: EventTarget): boolean =>
      target === window || target === document || target === document.body;
    // These listeners are torn down by AbortController, which never calls removeEventListener.
    EventTarget.prototype.addEventListener = function (type, listener, options) {
      if (type === "wheel" && global(this)) {
        live++;
        (options as { signal?: AbortSignal } | undefined)?.signal?.addEventListener("abort", () => {
          live--;
        });
      }
      add.call(this, type, listener, options);
    };
    EventTarget.prototype.removeEventListener = function (type, listener, options) {
      if (type === "wheel" && global(this)) live--;
      remove.call(this, type, listener, options);
    };
  });
  const { host, body } = await openAutoscrollPane(page, "autoscroll-listeners");
  const wheelListeners = () =>
    page.evaluate(() =>
      (window as unknown as { __wheelListeners: () => number }).__wheelListeners(),
    );

  try {
    expect(await wheelListeners()).toBe(0);

    const origin = await paneOrigin(body);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(body).toHaveClass(/middle-click-autoscrolling/);
    expect(await wheelListeners()).toBeGreaterThan(0);

    await page.keyboard.press("Escape");
    await expect(body).not.toHaveClass(/middle-click-autoscrolling/);
    await expect.poll(wheelListeners).toBe(0);
  } finally {
    await host.close();
  }
});

// The gesture belongs to the app, not to one pane: it takes whichever surface under the pointer can scroll —
// here the session rail, whose rows are buttons, so a click target that acts on its own left button still
// autoscrolls.
test("middle-click autoscrolls any scrollable surface, not just the transcript", async ({
  page,
}) => {
  // Two sessions load; the rest are chips, enough of them to overflow the rail.
  const sessions = Array.from({ length: 40 }, (_, index) => ({
    ...mockSession(`rail-${index}`, `rail-${index}`, "acp"),
    loaded: index < 2,
  }));
  const host = await MockHost.start({ distDir, sessions });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const rail = page.locator(".session-rail");
    await expect(page.locator(".session-chip")).toHaveCount(sessions.length);
    expect(
      await rail.evaluate((element) => element.scrollHeight - element.clientHeight),
    ).toBeGreaterThan(0);

    // Arming over a chip and dismissing it in place swallows the whole click: the scroll ends without also
    // switching to the session under the pointer. The same click switches once nothing is armed.
    const chip = page.locator('[data-session-slot="rail-1"]');
    const at = await paneOrigin(chip);
    await page.mouse.click(at.x, at.y, { button: "middle" });
    await expect(rail).toHaveClass(/middle-click-autoscrolling/);
    await page.mouse.click(at.x, at.y);
    await expect(rail).not.toHaveClass(/middle-click-autoscrolling/);
    await expect(chip).not.toHaveClass(/active/);
    await page.mouse.click(at.x, at.y);
    await expect(chip).toHaveClass(/active/);

    const origin = await paneOrigin(rail);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(rail).toHaveClass(/middle-click-autoscrolling/);
    await expect(page.locator(".middle-click-autoscroll-origin")).toBeVisible();
    await page.mouse.move(origin.x, origin.y + 120);
    await expect.poll(() => rail.evaluate((element) => element.scrollTop)).toBeGreaterThan(0);

    await page.keyboard.press("Escape");
    await expect(rail).not.toHaveClass(/middle-click-autoscrolling/);
    await expect(page.locator(".middle-click-autoscroll-origin")).toHaveCount(0);
  } finally {
    await host.close();
  }
});

// The editor's autoscroll is Monaco's own `scrollOnMiddleClick` contribution, wired to the same setting: its
// viewport is not a native scrollable element, so the app-level gesture skips it (`.monaco-editor` owns the
// middle button) and Monaco scrolls itself.
test("middle-click autoscrolls the editor and responds live", async ({ page }) => {
  const session = mockSession("editor-autoscroll", "editor-autoscroll", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });

  try {
    host.files.set(
      "/long.ts",
      Array.from({ length: 400 }, (_, index) => `export const line${index} = ${index};`).join("\n"),
    );
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    host.publishSession(session.address, "editor", "openFile", {
      path: "/long.ts",
      line: 1,
      preview: false,
      scratch: false,
    });

    const editor = page.locator(".monaco-editor").first();
    await expect(editor).toBeVisible();
    await expect(page.locator(".monaco-editor .view-lines").first()).toContainText("line0 = 0");
    const scrollTop = (): Promise<number> =>
      page.evaluate(() => window.__WEAVIE_EDITOR__?.getScrollTop() ?? -1);
    await expect.poll(scrollTop).toBe(0);

    const origin = await paneOrigin(editor);

    // A middle click arms the scroll; moving away from the click point drives it, faster the further you go.
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(editor).toHaveClass(/scroll-editor-on-middle-click-editor/);
    await page.mouse.move(origin.x, origin.y + 120);
    await expect.poll(scrollTop).toBeGreaterThan(0);

    // Any key ends the scroll, matching the transcript's Escape.
    await page.keyboard.press("Escape");
    await expect(editor).not.toHaveClass(/scroll-editor-on-middle-click-editor/);

    host.publishHost("settings", "editorOptions", {
      gitBlame: "off",
      middleClickAutoscroll: false,
    });
    await expect
      .poll(() =>
        page.evaluate(() => window.__WEAVIE_EDITOR__?.getRawOptions().scrollOnMiddleClick),
      )
      .toBe(false);
    await page.mouse.click(origin.x, origin.y, { button: "middle" });
    await expect(editor).not.toHaveClass(/scroll-editor-on-middle-click-editor/);
  } finally {
    await host.close();
  }
});

// The tab strip claims the middle button for close (`data-middle-click`). The app-level gesture sees the press
// first — capture phase, before the tab's own handler — so without that opt-out it would swallow the close.
test("middle-clicking an editor tab closes it instead of starting an autoscroll", async ({
  page,
}) => {
  const session = mockSession("editor-tabs", "editor-tabs", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });

  try {
    // Enough tabs to overflow the strip: the track then IS a scrollable surface, so the gesture would take
    // the press for itself (and the tab would never close) without the opt-out.
    const files = Array.from({ length: 14 }, (_, index) => `/module-${index}.ts`);
    for (const path of files) {
      host.files.set(path, "export const value = 1;\n");
    }
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    for (const path of files) {
      host.publishSession(session.address, "editor", "openFile", {
        path,
        line: 1,
        preview: false,
        scratch: false,
      });
    }
    await expect(page.locator(".editor-tab")).toHaveCount(files.length);
    const track = page.locator(".editor-tabs-track");
    expect(
      await track.evaluate((element) => element.scrollWidth - element.clientWidth),
    ).toBeGreaterThan(1);

    const tab = page.locator(".editor-tab", { hasText: "module-13.ts" });
    const origin = await paneOrigin(tab.locator(".editor-tab-main"));
    // Deliberately don't wait for editor readiness: the active tab is valid while its lazy model is still loading.
    await page.mouse.click(origin.x, origin.y, { button: "middle" });

    // The tab closes without arming autoscroll, and the editor finishes its asynchronous switch to the survivor.
    await expect(tab).toHaveCount(0);
    await expect
      .poll(() =>
        page.evaluate(
          () => window.__WEAVIE_EDITOR__?.getModel()?.uri.path.endsWith("/module-12.ts") ?? false,
        ),
      )
      .toBe(true);
    await expect(page.locator(".middle-click-autoscroll-origin")).toHaveCount(0);
    await expect(page.locator(".middle-click-autoscrolling")).toHaveCount(0);
  } finally {
    await host.close();
  }
});
