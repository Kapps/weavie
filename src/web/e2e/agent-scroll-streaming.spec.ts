import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page } from "@playwright/test";
import { test } from "./harness/network-fixtures";
import { MockHost, type MockLiveSession, mockSession } from "./mock-host";

// The pane is bottom-origin: the reader stays still while a turn streams below them, a touch gesture holds the
// transcript it is reading until the finger and its momentum are done, and following tracks the latest output.
// Every turn streams as many small deltas, the way real agents deliver a reply.

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

const history = Array.from({ length: 30 }, (_, turn) => [
  {
    providerId: "acp",
    type: "user-message",
    turnId: `h${turn}`,
    itemId: `u-${turn}`,
    text: `Prompt ${turn}`,
  },
  {
    providerId: "acp",
    type: "item-completed",
    turnId: `h${turn}`,
    itemId: `tool-${turn}`,
    itemType: "commandExecution",
    status: "completed",
    title: `grep -rn "thing" src/${turn}`,
    text: `src/file${turn}.ts: matched`,
  },
  {
    providerId: "acp",
    type: "item-completed",
    turnId: `h${turn}`,
    itemId: `a-${turn}`,
    itemType: "agentMessage",
    status: "completed",
    text: `### Answer ${turn}\n\n${"A paragraph that wraps in the pane. ".repeat((turn % 5) + 2)}[source ${turn}](https://example.com/${turn})`,
  },
]).flat();

async function openPane(
  page: Page,
  name: string,
): Promise<{ body: Locator; host: MockHost; session: MockLiveSession }> {
  const session = mockSession(name, name, "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, { generation: 1, batchSize: 5000, messages: history });
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  const compact = page.getByRole("button", { name: "Agent", exact: true });
  if (await compact.isVisible()) await compact.click();
  await expect(page.getByText("Answer 29", { exact: true })).toBeVisible();
  return { body: page.locator(".agent-body"), host, session };
}

const streamed = new Map<string, number>();

// Streams `count` more small deltas into one live reply, each in its own frame.
async function stream(
  page: Page,
  host: MockHost,
  session: MockLiveSession,
  itemId: string,
  count: number,
): Promise<void> {
  const start = streamed.get(itemId) ?? 0;
  streamed.set(itemId, start + count);
  for (let index = start; index < start + count; index++) {
    host.publishAgentPane(session.address, {
      providerId: "acp",
      type: "agent-message-delta",
      turnId: "live",
      itemId,
      itemType: "agentMessage",
      text: index % 6 === 5 ? `delta ${index}.\n\n` : `delta ${index} `,
    });
    await nextFrame(page);
  }
}

function nextFrame(page: Page): Promise<void> {
  return page.evaluate(
    () => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())),
  );
}

// The topmost visible row and its offset from the viewport top.
function readAnchor(element: HTMLElement): { id: string; top: number } {
  const viewport = element.getBoundingClientRect();
  const row = [...element.querySelectorAll<HTMLElement>(".agent-virtual-row")].find(
    (candidate) => candidate.getBoundingClientRect().bottom > viewport.top,
  );
  if (row === undefined) throw new Error("No visible transcript row");
  return { id: row.dataset.transcriptEntry!, top: row.getBoundingClientRect().top - viewport.top };
}

function anchorDrift(element: HTMLElement, anchor: { id: string; top: number }): number {
  const row = element.querySelector<HTMLElement>(
    `.agent-virtual-row[data-transcript-entry="${CSS.escape(anchor.id)}"]`,
  );
  if (row === null) return Number.POSITIVE_INFINITY;
  return Math.abs(
    row.getBoundingClientRect().top - element.getBoundingClientRect().top - anchor.top,
  );
}

// Resolves once the scroll position has held for two frames, so a wheel animation has finished.
async function untilStill(element: HTMLElement): Promise<void> {
  let previous = Number.NaN;
  for (let still = 0; still < 2; ) {
    await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
    still = element.scrollTop === previous ? still + 1 : 0;
    previous = element.scrollTop;
  }
}

async function readAway(page: Page, body: Locator): Promise<{ id: string; top: number }> {
  await body.hover();
  await page.mouse.wheel(0, -1500);
  await expect.poll(() => body.evaluate((element) => -element.scrollTop)).toBeGreaterThan(1000);
  await body.evaluate(untilStill);
  return body.evaluate(readAnchor);
}

test("reading while a turn streams keeps the reader still and announces the output", async ({
  page,
}) => {
  const { body, host, session } = await openPane(page, "desktop-reading");
  try {
    await stream(page, host, session, "live-reply", 10);
    const anchor = await readAway(page, body);
    const latest = page.getByRole("button", { name: "Jump to latest", exact: true });
    await expect(latest).not.toContainText("New output");
    const height = await body.evaluate((element) => element.scrollHeight);

    const drift = await body.evaluateHandle((element, start) => {
      let worst = 0;
      const sample = (): void => {
        const row = element.querySelector<HTMLElement>(
          `.agent-virtual-row[data-transcript-entry="${CSS.escape(start.id)}"]`,
        );
        const top = row === null ? Number.POSITIVE_INFINITY : row.getBoundingClientRect().top;
        worst = Math.max(worst, Math.abs(top - element.getBoundingClientRect().top - start.top));
        frame = requestAnimationFrame(sample);
      };
      let frame = requestAnimationFrame(sample);
      return {
        stop: () => {
          cancelAnimationFrame(frame);
          return worst;
        },
      };
    }, anchor);
    await stream(page, host, session, "live-reply", 60);
    expect(
      await drift.evaluate((sampler) => sampler.stop()),
      "the reader moved",
    ).toBeLessThanOrEqual(1);
    // Output below the reader is applied as it arrives, not held.
    expect(await body.evaluate((element) => element.scrollHeight)).toBeGreaterThan(height);
    await expect(latest).toContainText("New output");

    // Output landing mid-wheel is compensated too: the content moves by exactly the wheel's distance.
    const wheeled = await body.evaluate(readAnchor);
    await page.mouse.wheel(0, -120);
    await stream(page, host, session, "live-reply", 12);
    await body.evaluate(untilStill);
    expect(Math.abs((await body.evaluate(anchorDrift, wheeled)) - 120)).toBeLessThanOrEqual(1);
    await expect(latest).toContainText("New output");

    await latest.click();
    await expect.poll(() => body.evaluate((element) => -element.scrollTop)).toBeLessThanOrEqual(1);
    await expect(latest).toHaveCount(0);
    await expect(page.getByText("delta 81", { exact: false })).toBeInViewport();
    await stream(page, host, session, "live-reply", 10);
    expect(await body.evaluate((element) => -element.scrollTop)).toBeLessThanOrEqual(1);
  } finally {
    await host.close();
  }
});

test("a request arriving while the reader is away keeps their position", async ({ page }) => {
  const { body, host, session } = await openPane(page, "request-reading");
  try {
    const anchor = await readAway(page, body);
    host.publishAgentPane(session.address, {
      providerId: "acp",
      type: "approval-requested",
      turnId: "live",
      itemId: "approval",
      requestId: "approval",
      status: "pending",
      summary: "Wants to run the test suite.",
    });
    await expect(body.locator(".agent-entry-request")).toHaveCount(1);
    await nextFrame(page);
    expect(await body.evaluate(anchorDrift, anchor)).toBeLessThanOrEqual(1);
    await expect(body.locator(".agent-entry-request")).not.toBeInViewport();
  } finally {
    await host.close();
  }
});

test("a long live reply is pinned at its top and keeps it there as it grows", async ({ page }) => {
  const { host, session } = await openPane(page, "pin-reply");
  try {
    const reply = page.locator(".agent-virtual-row", { hasText: "delta 0 " });
    const top = () =>
      reply.evaluate(
        (row) =>
          row.getBoundingClientRect().top - row.closest(".agent-body")!.getBoundingClientRect().top,
      );
    await stream(page, host, session, "long-reply", 400);
    await expect(page.getByRole("button", { name: "Jump to latest", exact: true })).toBeVisible();
    expect(Math.abs(await top())).toBeLessThanOrEqual(1);
    await stream(page, host, session, "long-reply", 40);
    expect(Math.abs(await top())).toBeLessThanOrEqual(1);
  } finally {
    await host.close();
  }
});

test("following the latest output stays at the bottom and keyboard keys scroll the pane", async ({
  page,
}) => {
  const { body, host, session } = await openPane(page, "keyboard");
  try {
    await stream(page, host, session, "follow", 30);
    expect(await body.evaluate((element) => element.scrollTop)).toBe(0);
    // Keys act wherever focus sits in the transcript, short of a field that takes them.
    await body.evaluate((element) =>
      element.querySelector<HTMLElement>("summary:last-of-type")!.focus({ preventScroll: true }),
    );
    // A focused control keeps focus while the keys scroll.
    await page.keyboard.press("ArrowUp");
    expect(
      await body.evaluate(
        (element) =>
          document.activeElement?.closest(".agent-body") === element &&
          document.activeElement !== element,
      ),
    ).toBe(true);
    await page.keyboard.press("Home");
    await expect
      .poll(() =>
        body.evaluate((element) => element.scrollTop + element.scrollHeight - element.clientHeight),
      )
      .toBeLessThanOrEqual(1);
    await expect(page.getByText("Prompt 0", { exact: true })).toBeInViewport();
    const latest = page.getByRole("button", { name: "Jump to latest", exact: true });
    await expect(latest).toHaveCount(1);
    const before = await body.evaluate((element) => element.scrollTop);
    await page.keyboard.press("PageDown");
    await expect
      .poll(() => body.evaluate((element, start) => element.scrollTop - start, before))
      .toBeGreaterThan(200);
    await page.keyboard.press("End");
    await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBe(0);
    await expect(latest).toHaveCount(0);
    await stream(page, host, session, "follow", 10);
    expect(await body.evaluate((element) => element.scrollTop)).toBe(0);
  } finally {
    await host.close();
  }
});

test("expanding details while reading keeps the toggled header still", async ({ page }) => {
  const { body, host } = await openPane(page, "details");
  try {
    // While following, a toggle changes nothing about following.
    await body.locator(".agent-activity-details > summary").last().click();
    await expect(page.getByRole("button", { name: "Jump to latest", exact: true })).toHaveCount(0);
    await body.locator(".agent-activity-details > summary").last().click();
    await readAway(page, body);
    const visible = await body.evaluate((element) => {
      const viewport = element.getBoundingClientRect();
      return [...element.querySelectorAll(".agent-activity-details > summary")].findIndex(
        (summary) => {
          const bounds = summary.getBoundingClientRect();
          return bounds.top > viewport.top && bounds.bottom < viewport.bottom;
        },
      );
    });
    const summary = body.locator(".agent-activity-details > summary").nth(visible);
    await expect(summary).toBeInViewport();
    const before = await summary.evaluate((element) => element.getBoundingClientRect().top);
    await summary.click();
    await expect(body.locator(".agent-activity-step").first()).toBeVisible();
    await nextFrame(page);
    expect(
      Math.abs((await summary.evaluate((element) => element.getBoundingClientRect().top)) - before),
    ).toBeLessThanOrEqual(1);
    // The reader's own toggle is not new output.
    await expect(
      page.getByRole("button", { name: "Jump to latest", exact: true }),
    ).not.toContainText("New output");
  } finally {
    await host.close();
  }
});

test("focusing a link in an older row stops following, and streaming leaves it in view", async ({
  page,
}) => {
  const { body, host, session } = await openPane(page, "focus-reading");
  try {
    await stream(page, host, session, "focus-reply", 5);
    const link = body.getByRole("link", { name: "source 27", exact: true });
    await link.focus();
    await expect(link).toBeInViewport();
    await expect(page.getByRole("button", { name: "Jump to latest", exact: true })).toBeVisible();
    await stream(page, host, session, "focus-reply", 30);
    await expect(link).toBeInViewport();
    // Output elsewhere leaves an unchanged row's rendered content, and the focus in it, alone.
    await expect(link).toBeFocused();
  } finally {
    await host.close();
  }
});

test("a rewind while reading starts the new transcript following, with nothing unseen", async ({
  page,
}) => {
  const { body, host, session } = await openPane(page, "rewind-reading");
  try {
    await readAway(page, body);
    await stream(page, host, session, "before-rewind", 20);
    const latest = page.getByRole("button", { name: "Jump to latest", exact: true });
    await expect(latest).toContainText("New output");
    host.setAgentHistory(session.address, {
      generation: 2,
      batchSize: 5000,
      messages: history.slice(0, 45),
    });
    host.publishSession(session.address, "agent", "paneReset", {});
    await expect(page.getByText("Answer 14", { exact: true })).toBeInViewport();
    await expect(latest).toHaveCount(0);
    expect(await body.evaluate((element) => element.scrollTop)).toBe(0);
    await stream(page, host, session, "after-rewind", 10);
    expect(await body.evaluate((element) => element.scrollTop)).toBe(0);
    await expect(latest).toHaveCount(0);
  } finally {
    await host.close();
  }
});

test("a short transcript sits at the top of the pane", async ({ page }) => {
  const session = mockSession("short", "short", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, {
    generation: 1,
    batchSize: 50,
    messages: history.slice(0, 3),
  });
  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(page.getByText("Answer 0", { exact: true })).toBeVisible();
    const offset = await body.evaluate(
      (element) =>
        element.querySelector(".agent-virtual-row")!.getBoundingClientRect().top -
        element.getBoundingClientRect().top,
    );
    expect(Math.abs(offset)).toBeLessThanOrEqual(1);
  } finally {
    await host.close();
  }
});

test("returning to a session restores the reading position and applies output that arrived", async ({
  page,
}) => {
  const first = mockSession("restore-first", "restore-first", "acp");
  const second = mockSession("restore-second", "restore-second", "acp");
  const host = await MockHost.start({ distDir, sessions: [first, second] });
  host.setAgentHistory(first.address, { generation: 1, batchSize: 5000, messages: history });
  host.setAgentHistory(second.address, {
    generation: 1,
    batchSize: 50,
    messages: history.slice(0, 3),
  });
  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(page.getByText("Answer 29", { exact: true })).toBeVisible();
    const anchor = await readAway(page, body);
    await page.locator('.session-chip[title^="restore-second —"]').click();
    await expect(page.getByText("Answer 0", { exact: true })).toBeVisible();
    await stream(page, host, first, "away", 40);
    await page.locator('.session-chip[title^="restore-first —"]').click();
    await expect(page.getByRole("button", { name: "Jump to latest", exact: true })).toContainText(
      "New output",
    );
    await expect.poll(() => body.evaluate(anchorDrift, anchor)).toBeLessThanOrEqual(1);
  } finally {
    await host.close();
  }
});

test.describe("touch on iOS", () => {
  test.use({
    viewport: { width: 390, height: 844 },
    hasTouch: true,
    userAgent:
      "Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1",
  });

  test("a touch gesture holds the transcript still until it ends, then applies the output", async ({
    page,
  }) => {
    const { body, host, session } = await openPane(page, "touch-hold");
    try {
      await stream(page, host, session, "held-reply", 10);
      const touch = (type: string, active: boolean) =>
        body.evaluate(
          (element, event) => {
            const finger = [new Touch({ identifier: 1, target: element })];
            const touches = event.active ? finger : [];
            element.dispatchEvent(
              new TouchEvent(event.type, { bubbles: true, touches, changedTouches: finger }),
            );
          },
          { type, active },
        );
      await touch("touchstart", true);
      for (let step = 0; step < 30; step++) {
        await body.evaluate((element) => {
          element.scrollTop -= 40;
        });
        await nextFrame(page);
      }
      const anchor = await body.evaluate(readAnchor);
      const height = await body.evaluate((element) => element.scrollHeight);
      await stream(page, host, session, "held-reply", 30);
      host.publishAgentPane(session.address, {
        providerId: "acp",
        type: "item-completed",
        turnId: "live",
        itemId: "held-marker",
        itemType: "agentMessage",
        status: "completed",
        text: "Held marker",
      });
      await nextFrame(page);
      // Held: the transcript the reader touches doesn't change under the finger.
      expect(await body.evaluate((element) => element.scrollHeight)).toBe(height);
      expect(await body.evaluate(anchorDrift, anchor)).toBeLessThanOrEqual(1);

      await touch("touchend", false);
      await expect
        .poll(() => body.evaluate((element) => element.scrollHeight))
        .toBeGreaterThan(height);
      expect(await body.evaluate(anchorDrift, anchor)).toBeLessThanOrEqual(1);
      const latest = page.getByRole("button", { name: "Jump to latest", exact: true });
      await expect(latest).toContainText("New output");
      await latest.tap();
      await expect
        .poll(() => body.evaluate((element) => -element.scrollTop))
        .toBeLessThanOrEqual(1);
      await expect(page.getByText("Held marker", { exact: true })).toBeInViewport();
    } finally {
      await host.close();
    }
  });

  test("a dragging finger holds through its momentum; a resting one releases as it lifts", async ({
    page,
  }) => {
    const { body, host, session } = await openPane(page, "touch-momentum");
    try {
      await stream(page, host, session, "momentum-reply", 10);
      // Synthetic events alone, so no real scroll sends a scrollend the test didn't ask for.
      const send = (type: string, active: boolean) =>
        body.evaluate(
          (element, event) => {
            const finger = [new Touch({ identifier: 1, target: element })];
            const init = {
              bubbles: true,
              touches: event.active ? finger : [],
              changedTouches: finger,
            };
            element.dispatchEvent(
              event.type.startsWith("touch")
                ? new TouchEvent(event.type, init)
                : new Event(event.type),
            );
          },
          { type, active },
        );
      const height = () => body.evaluate((element) => element.scrollHeight);
      await send("touchstart", true);
      await body.evaluate((element) => {
        element.scrollTop -= 1200;
      });
      await expect(page.getByRole("button", { name: "Jump to latest", exact: true })).toBeVisible();
      await expect.poll(() => body.evaluate((element) => element.scrollTop)).toBeLessThan(-1000);
      // A scroll the layout caused while the finger rests is not the finger's.
      await send("scroll", true);
      const resting = await height();
      await stream(page, host, session, "momentum-reply", 10);
      expect(await height()).toBe(resting);
      await send("touchend", false);
      await expect.poll(height).toBeGreaterThan(resting);

      // The engine's own scrollend for the drag would end it at once; momentum ends when the test says.
      await page.evaluate(() =>
        window.addEventListener(
          "scrollend",
          (event) => event.isTrusted && event.stopImmediatePropagation(),
          { capture: true },
        ),
      );
      await send("touchstart", true);
      await send("touchmove", true);
      await body.evaluate((element) => {
        element.scrollTop -= 200;
      });
      await nextFrame(page);
      const dragged = await height();
      await stream(page, host, session, "momentum-reply", 10);
      await send("touchend", false);
      await nextFrame(page);
      await nextFrame(page);
      // Momentum: the finger is up but the pane is still moving under it.
      expect(await height()).toBe(dragged);
      await send("scrollend", false);
      await expect.poll(height).toBeGreaterThan(dragged);
    } finally {
      await host.close();
    }
  });

  // Engines send no scrollend for a scroll the layout caused, so such a scroll must never hold past the lift.
  test("a layout clamp under a finger that dragged without scrolling releases as it lifts", async ({
    page,
  }) => {
    const { body, host, session } = await openPane(page, "touch-clamp");
    try {
      await stream(page, host, session, "clamp-reply", 10);
      const send = (type: string, active: boolean) =>
        body.evaluate(
          (element, event) => {
            const finger = [new Touch({ identifier: 1, target: element })];
            const touches = event.active ? finger : [];
            element.dispatchEvent(
              new TouchEvent(event.type, { bubbles: true, touches, changedTouches: finger }),
            );
          },
          { type, active },
        );
      const height = () => body.evaluate((element) => element.scrollHeight);
      const viewport = page.viewportSize()!;
      await send("touchstart", true);
      // The finger rests at the very top, where a taller viewport clamps the scroll position.
      await body.evaluate((element) => {
        element.scrollTop = -element.scrollHeight;
      });
      await expect(page.getByText("Prompt 0", { exact: true })).toBeInViewport();
      const resting = await height();
      await stream(page, host, session, "clamp-reply", 10);
      expect(await height()).toBe(resting);
      // A drag the pane doesn't follow (a nested scroller, an edge), then a clamp, no scrollend.
      await send("touchmove", true);
      const clamped = body.evaluate(
        (element) =>
          new Promise<number>((resolve) =>
            element.addEventListener("scroll", () => resolve(element.scrollTop), { once: true }),
          ),
      );
      await page.setViewportSize({ width: viewport.width, height: viewport.height + 120 });
      await clamped;
      await send("touchend", false);
      await expect.poll(height).toBeGreaterThan(resting);
    } finally {
      await host.close();
    }
  });
});
