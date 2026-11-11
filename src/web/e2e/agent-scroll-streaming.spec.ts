import { expect, type Locator, type Page } from "@playwright/test";
import {
  type AgentPane,
  agentTurns,
  anchorMovement,
  iPhone,
  type OpenPanes,
  publishDelta,
  readAnchor,
  test,
  untilStill,
  wheelUp,
} from "./harness/agent-pane";
import { frames } from "./harness/render-budget";
import { fromReplyStart } from "./harness/reply-start";

// The pane is bottom-origin: the reader stays still while a turn streams below them, a touch gesture holds the
// transcript it is reading until the finger and its momentum are done, and following tracks the latest output.
// Every turn streams as many small deltas, the way real agents deliver a reply.

const history = agentTurns(30);

async function openPane(openPanes: OpenPanes, name: string): Promise<AgentPane> {
  const pane = await openPanes({ [name]: history });
  const compact = pane.page.getByRole("button", { name: "Agent", exact: true });
  if (await compact.isVisible()) await compact.click();
  await expect(pane.page.getByText("Answer 29", { exact: true })).toBeVisible();
  return pane;
}

// Streams `count` more small deltas into one live reply, each in its own frame.
async function stream(pane: AgentPane, itemId: string, count: number): Promise<void> {
  for (let step = 0; step < count; step++) {
    publishDelta(pane, itemId);
    await frames(pane.page, 1);
  }
}

async function readAway(pane: AgentPane): Promise<{ id: string; top: number }> {
  await wheelUp(pane, 1500, 1000);
  return pane.body.evaluate(readAnchor);
}

const jumpToLatest = (page: Page): Locator =>
  page.getByRole("button", { name: "Jump to latest", exact: true });
const scrollTop = (body: Locator): Promise<number> => body.evaluate((element) => element.scrollTop);
const fromBottom = (body: Locator): Promise<number> =>
  body.evaluate((element) => -element.scrollTop);
const scrollHeight = (body: Locator): Promise<number> =>
  body.evaluate((element) => element.scrollHeight);
const drift = async (body: Locator, anchor: { id: string; top: number }): Promise<number> =>
  Math.abs(await body.evaluate(anchorMovement, anchor));

test("reading while a turn streams keeps the reader still and announces the output", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "desktop-reading");
  const { body } = pane;
  await stream(pane, "live-reply", 10);
  const anchor = await readAway(pane);
  const latest = jumpToLatest(page);
  await expect(latest).not.toContainText("New output");
  const height = await scrollHeight(body);

  const sampler = await body.evaluateHandle((element, start) => {
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
  await stream(pane, "live-reply", 60);
  expect(
    await sampler.evaluate((running) => running.stop()),
    "the reader moved",
  ).toBeLessThanOrEqual(1);
  // Output below the reader is applied as it arrives, not held.
  expect(await scrollHeight(body)).toBeGreaterThan(height);
  await expect(latest).toContainText("New output");

  // Output landing mid-wheel is compensated too: the content moves by exactly the wheel's distance.
  const wheeled = await body.evaluate(readAnchor);
  await page.mouse.wheel(0, -120);
  await stream(pane, "live-reply", 12);
  await body.evaluate(untilStill);
  expect(Math.abs((await drift(body, wheeled)) - 120)).toBeLessThanOrEqual(1);
  await expect(latest).toContainText("New output");

  await latest.click();
  await expect.poll(() => fromBottom(body)).toBeLessThanOrEqual(1);
  await expect(latest).toHaveCount(0);
  await expect(page.getByText("delta 81", { exact: false })).toBeInViewport();
  await stream(pane, "live-reply", 10);
  expect(await fromBottom(body)).toBeLessThanOrEqual(1);
});

for (const input of ["trackpad", "native"] as const) {
  test(`small upward ${input} scrolling stays paused while a side conversation streams`, async ({
    page,
    openPanes,
  }) => {
    const pane = await openPane(openPanes, "nested-reading");
    const { body, host, session } = pane;
    const side = {
      providerId: "acp",
      conversationId: "side",
      anchorTurnId: "h29",
      threadId: "side-thread",
      turnId: "1",
      isPrimaryThread: false,
    };
    host.publishAgentPane(session.address, {
      ...side,
      type: "side-conversation-started",
      status: "forking",
      text: "Investigate this in a separate conversation",
    });
    host.publishAgentPane(session.address, { ...side, type: "turn-started" });
    const publish = (text: string): void =>
      host.publishAgentPane(session.address, {
        ...side,
        type: "agent-message-delta",
        itemId: "side-reply",
        itemType: "agentMessage",
        text,
      });
    publish("The side conversation is reporting its findings.");
    const reply = body.locator(".agent-aside .agent-tone-assistant").last();
    await expect(reply).toContainText("reporting its findings");
    await reply.hover();
    const latest = jumpToLatest(page);
    const scrollBy = async (delta: number): Promise<void> => {
      if (input === "trackpad") await page.mouse.wheel(0, delta);
      else
        await body.evaluate((element, pixels) => {
          element.scrollTop += pixels;
        }, delta);
      await body.evaluate(untilStill);
    };
    expect(await scrollTop(body)).toBe(0);

    // Trackpads can leave the bottom in steps smaller than the three-line following threshold.
    await scrollBy(-12.5);
    await expect.poll(() => fromBottom(body)).toBeGreaterThan(10);
    await expect(latest).toBeVisible();
    const anchor = await body.evaluate(readAnchor);
    publish(" More findings arrive while the reader is scrolling upward.");
    await expect(reply).toContainText("scrolling upward");
    await frames(page, 2);
    expect(await drift(body, anchor)).toBeLessThanOrEqual(1);
    await expect(latest).toContainText("New output");

    await scrollBy(-12.5);
    const farther = await fromBottom(body);
    publish(" The reader can continue moving toward earlier messages.");
    await expect(reply).toContainText("earlier messages");
    await frames(page, 2);
    expect(await fromBottom(body)).toBeGreaterThanOrEqual(farther - 1);

    await latest.click();
    await expect.poll(() => scrollTop(body)).toBe(0);
    await expect(latest).toHaveCount(0);
    publish(" Following resumes when requested.");
    await expect(reply).toContainText("when requested");
    await frames(page, 2);
    expect(await scrollTop(body)).toBe(0);

    await reply.hover();
    await scrollBy(-12.5);
    await expect(latest).toBeVisible();
    await scrollBy(5.5);
    await expect(latest).toHaveCount(0);
    publish(" Scrolling back toward the bottom resumes following too.");
    await expect(reply).toContainText("following too");
    await frames(page, 2);
    expect(await scrollTop(body)).toBe(0);
  });
}

test("a request arriving while the reader is away keeps their position", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "request-reading");
  const { body } = pane;
  const anchor = await readAway(pane);
  pane.host.publishAgentPane(pane.session.address, {
    providerId: "acp",
    type: "approval-requested",
    turnId: "live",
    itemId: "approval",
    requestId: "approval",
    status: "pending",
    summary: "Wants to run the test suite.",
  });
  await expect(body.locator(".agent-entry-request")).toHaveCount(1);
  await frames(page, 1);
  expect(await drift(body, anchor)).toBeLessThanOrEqual(1);
  await expect(body.locator(".agent-entry-request")).not.toBeInViewport();
});

test("a long live reply is pinned at its start line and keeps it there as it grows", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "pin-reply");
  const reply = page.locator(".agent-virtual-row", { hasText: "delta 0 " });
  await stream(pane, "long-reply", 400);
  await expect(jumpToLatest(page)).toBeVisible();
  expect(Math.abs(await fromReplyStart(reply))).toBeLessThanOrEqual(1);
  await stream(pane, "long-reply", 40);
  expect(Math.abs(await fromReplyStart(reply))).toBeLessThanOrEqual(1);
  // What came before the reply stays in view above it.
  await expect(page.locator(".agent-virtual-row", { hasText: "Answer 29" })).toBeInViewport();
});

test("a reply start offset of 0 pins a long live reply against the top of the pane", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "pin-top");
  const defaults = await page.evaluate(() => window.__WEAVIE_AGENT__);
  pane.host.publishHost("settings", "agent-defaults", { ...defaults, replyStartOffset: 0 });
  await stream(pane, "long-reply", 400);
  await expect(jumpToLatest(page)).toBeVisible();
  const top = await page
    .locator(".agent-virtual-row", { hasText: "delta 0 " })
    .evaluate(
      (row) =>
        row.getBoundingClientRect().top - row.closest(".agent-body")!.getBoundingClientRect().top,
    );
  expect(Math.abs(top)).toBeLessThanOrEqual(1);
});

test("jumping to the latest output reaches the end of a reply that arrived while reading", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "jump-past-reply");
  await readAway(pane);
  await stream(pane, "while-away", 60);
  await jumpToLatest(page).click();
  await expect.poll(() => scrollTop(pane.body)).toBe(0);
  await stream(pane, "while-away", 6);
  expect(await scrollTop(pane.body)).toBe(0);
  await expect(jumpToLatest(page)).toHaveCount(0);
});

test("the transcript is never shorter while its rows update than once they have", async ({
  page,
  openPanes,
}) => {
  // WebKitGTK lays out between a row's removal and the spacer that replaces it, and clamps a reader near the
  // top to the shorter transcript. This lays out at every removal and again once the update is done.
  await page.addInitScript(() => {
    const shortfalls: number[] = [];
    Object.assign(window, { shortfalls });
    const { remove } = Element.prototype;
    Element.prototype.remove = function (this: Element): void {
      remove.call(this);
      if (!this.classList.contains("agent-virtual-row")) return;
      const body = document.querySelector(".agent-body")!;
      const during = body.scrollHeight;
      queueMicrotask(() => shortfalls.push(body.scrollHeight - during));
    };
  });
  const pane = await openPanes({ "row-updates": history });
  const { body } = pane;
  await expect(page.getByText("Answer 29", { exact: true })).toBeVisible();
  await wheelUp(pane, 1500, 1000);
  await wheelUp(pane, 1500, 2000);
  const shortfalls = await page.evaluate(
    () => (window as unknown as { shortfalls: number[] }).shortfalls,
  );
  expect(shortfalls.length).toBeGreaterThan(0);
  expect(Math.max(...shortfalls)).toBeLessThanOrEqual(1);
  expect(await fromBottom(body)).toBeGreaterThan(2000);
});

test("a long reply loaded with the history is not pinned once the session goes live", async ({
  page,
  openPanes,
}) => {
  const long = Array.from({ length: 40 }, (_, line) => `Paragraph ${line} of the reply.`);
  const pane = await openPanes({
    "loaded-reply": history.map((message) =>
      message.itemId === "a-29" ? { ...message, text: long.join("\n\n") } : message,
    ),
  });
  const { body } = pane;
  await expect(page.getByText("Paragraph 39 of the reply.", { exact: true })).toBeVisible();
  expect(await scrollTop(body)).toBe(0);
  // The first live message after loading, then a taller pane as a sent prompt's composer collapses.
  pane.host.publishAgentPane(pane.session.address, {
    providerId: "acp",
    type: "session-info",
    summary: "Loaded reply",
  });
  await frames(page, 2);
  await page.setViewportSize({ width: 1280, height: 760 });
  await frames(page, 3);
  expect(await scrollTop(body)).toBe(0);
  await expect(jumpToLatest(page)).toHaveCount(0);
});

test("following the latest output stays at the bottom and keyboard keys scroll the pane", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "keyboard");
  const { body } = pane;
  // Short enough to stay below its start line, where a longer reply would stop following.
  await stream(pane, "follow", 6);
  expect(await scrollTop(body)).toBe(0);
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
  const latest = jumpToLatest(page);
  await expect(latest).toHaveCount(1);
  const before = await scrollTop(body);
  await page.keyboard.press("PageDown");
  await expect.poll(async () => (await scrollTop(body)) - before).toBeGreaterThan(200);
  await page.keyboard.press("End");
  await expect.poll(() => scrollTop(body)).toBe(0);
  await expect(latest).toHaveCount(0);
  await stream(pane, "follow", 6);
  expect(await scrollTop(body)).toBe(0);
});

test("expanding details while reading keeps the toggled header still", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "details");
  const { body } = pane;
  const summaries = body.locator(".agent-activity-details > summary");
  // While following, a toggle changes nothing about following.
  await summaries.last().click();
  await expect(jumpToLatest(page)).toHaveCount(0);
  await summaries.last().click();
  await readAway(pane);
  const visible = await body.evaluate((element) => {
    const viewport = element.getBoundingClientRect();
    return [...element.querySelectorAll(".agent-activity-details > summary")].findIndex(
      (summary) => {
        const bounds = summary.getBoundingClientRect();
        return bounds.top > viewport.top && bounds.bottom < viewport.bottom;
      },
    );
  });
  const summary = summaries.nth(visible);
  await expect(summary).toBeInViewport();
  const before = await summary.evaluate((element) => element.getBoundingClientRect().top);
  await summary.click();
  await expect(body.locator(".agent-activity-step").first()).toBeVisible();
  await frames(page, 1);
  expect(
    Math.abs((await summary.evaluate((element) => element.getBoundingClientRect().top)) - before),
  ).toBeLessThanOrEqual(1);
  // The reader's own toggle is not new output.
  await expect(jumpToLatest(page)).not.toContainText("New output");
});

test("focusing a link in an older row stops following, and streaming leaves it in view", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "focus-reading");
  await stream(pane, "focus-reply", 5);
  const link = pane.body.getByRole("link", { name: "source 27", exact: true });
  await link.focus();
  await expect(link).toBeInViewport();
  await expect(jumpToLatest(page)).toBeVisible();
  await stream(pane, "focus-reply", 30);
  await expect(link).toBeInViewport();
  // Output elsewhere leaves an unchanged row's rendered content, and the focus in it, alone.
  await expect(link).toBeFocused();
});

test("a rewind while reading starts the new transcript following, with nothing unseen", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(openPanes, "rewind-reading");
  const { body, host, session } = pane;
  await readAway(pane);
  await stream(pane, "before-rewind", 20);
  const latest = jumpToLatest(page);
  await expect(latest).toContainText("New output");
  host.setAgentHistory(session.address, {
    generation: 2,
    batchSize: 5000,
    messages: history.slice(0, 45),
  });
  host.publishSession(session.address, "agent", "paneReset", {});
  await expect(page.getByText("Answer 14", { exact: true })).toBeInViewport();
  await expect(latest).toHaveCount(0);
  expect(await scrollTop(body)).toBe(0);
  await stream(pane, "after-rewind", 10);
  expect(await scrollTop(body)).toBe(0);
  await expect(latest).toHaveCount(0);
});

test("a short transcript sits at the top of the pane", async ({ page, openPanes }) => {
  const { body } = await openPanes({ short: history.slice(0, 3) });
  await expect(page.getByText("Answer 0", { exact: true })).toBeVisible();
  const offset = await body.evaluate(
    (element) =>
      element.querySelector(".agent-virtual-row")!.getBoundingClientRect().top -
      element.getBoundingClientRect().top,
  );
  expect(Math.abs(offset)).toBeLessThanOrEqual(1);
});

test("returning to a session restores the reading position and applies output that arrived", async ({
  page,
  openPanes,
}) => {
  const pane = await openPanes({
    "restore-first": history,
    "restore-second": history.slice(0, 3),
  });
  await expect(page.getByText("Answer 29", { exact: true })).toBeVisible();
  const anchor = await readAway(pane);
  await page.locator('.session-chip[title^="restore-second —"]').click();
  await expect(page.getByText("Answer 0", { exact: true })).toBeVisible();
  await stream(pane, "away", 40);
  await page.locator('.session-chip[title^="restore-first —"]').click();
  await expect(jumpToLatest(page)).toContainText("New output");
  await expect(
    pane.body.locator(`[data-transcript-entry=${JSON.stringify(anchor.id)}]`),
  ).toBeAttached();
  await expect.poll(() => drift(pane.body, anchor)).toBeLessThanOrEqual(1);
});

test.describe("touch on iOS", () => {
  test.use(iPhone);

  // Synthetic, so the test alone decides when a touch, scroll, or scrollend happens.
  const sendAndScroll = (body: Locator, type: string, active: boolean, distance: number) =>
    body.evaluate(
      (element, event) => {
        const finger = [new Touch({ identifier: 1, target: element })];
        const init = {
          bubbles: true,
          touches: event.active ? finger : [],
          changedTouches: finger,
        };
        element.dispatchEvent(
          event.type.startsWith("touch") ? new TouchEvent(event.type, init) : new Event(event.type),
        );
        element.scrollTop -= event.distance;
      },
      { type, active, distance },
    );
  const send = (body: Locator, type: string, active: boolean) =>
    sendAndScroll(body, type, active, 0);
  // A finger's move and the scroll it drives are one task, so no scroll the layout caused lands between them.
  const drag = (body: Locator, distance: number) =>
    sendAndScroll(body, "touchmove", true, distance);

  test("a touch gesture holds the transcript still until it ends, then applies the output", async ({
    page,
    openPanes,
  }) => {
    const pane = await openPane(openPanes, "touch-hold");
    const { body } = pane;
    await stream(pane, "held-reply", 10);
    await send(body, "touchstart", true);
    for (let step = 0; step < 30; step++) {
      await body.evaluate((element) => {
        element.scrollTop -= 40;
      });
      await frames(page, 1);
    }
    const anchor = await body.evaluate(readAnchor);
    const height = await scrollHeight(body);
    await stream(pane, "held-reply", 30);
    pane.host.publishAgentPane(pane.session.address, {
      providerId: "acp",
      type: "item-completed",
      turnId: "live",
      itemId: "held-marker",
      itemType: "agentMessage",
      status: "completed",
      text: "Held marker",
    });
    await frames(page, 1);
    // Held: the transcript the reader touches doesn't change under the finger.
    expect(await scrollHeight(body)).toBe(height);
    expect(await drift(body, anchor)).toBeLessThanOrEqual(1);

    await send(body, "touchend", false);
    await expect.poll(() => scrollHeight(body)).toBeGreaterThan(height);
    expect(await drift(body, anchor)).toBeLessThanOrEqual(1);
    const latest = jumpToLatest(page);
    await expect(latest).toContainText("New output");
    await latest.tap();
    await expect.poll(() => fromBottom(body)).toBeLessThanOrEqual(1);
    await expect(page.getByText("Held marker", { exact: true })).toBeInViewport();
  });

  test("a dragging finger holds through its momentum; a resting one releases as it lifts", async ({
    page,
    openPanes,
  }) => {
    const pane = await openPane(openPanes, "touch-momentum");
    const { body } = pane;
    await stream(pane, "momentum-reply", 10);
    await send(body, "touchstart", true);
    await body.evaluate((element) => {
      element.scrollTop -= 1200;
    });
    await expect(jumpToLatest(page)).toBeVisible();
    await expect.poll(() => scrollTop(body)).toBeLessThan(-1000);
    // A scroll the layout caused while the finger rests is not the finger's.
    await send(body, "scroll", true);
    const resting = await scrollHeight(body);
    await stream(pane, "momentum-reply", 10);
    expect(await scrollHeight(body)).toBe(resting);
    await send(body, "touchend", false);
    await expect.poll(() => scrollHeight(body)).toBeGreaterThan(resting);

    // The engine's own scrollend for the drag would end it at once; momentum ends when the test says.
    await page.evaluate(() =>
      window.addEventListener(
        "scrollend",
        (event) => event.isTrusted && event.stopImmediatePropagation(),
        { capture: true },
      ),
    );
    await send(body, "touchstart", true);
    await drag(body, 200);
    await frames(page, 1);
    const dragged = await scrollHeight(body);
    await stream(pane, "momentum-reply", 10);
    await send(body, "touchend", false);
    await frames(page, 2);
    // Momentum: the finger is up but the pane is still moving under it.
    expect(await scrollHeight(body)).toBe(dragged);
    await send(body, "scrollend", false);
    await expect.poll(() => scrollHeight(body)).toBeGreaterThan(dragged);
  });

  // Engines send no scrollend for a scroll the layout caused, so such a scroll must never hold past the lift.
  // Flaked on Linux CI (2026-11-07 07:54Z, run https://github.com/Kapps/weavie/actions/runs/42352676339): the
  // scroll listener was attached by an un-awaited evaluate and could miss the clamp it waited for; it is now armed first.
  test("a layout clamp under a finger that dragged without scrolling releases as it lifts", async ({
    page,
    openPanes,
  }) => {
    const pane = await openPane(openPanes, "touch-clamp");
    const { body } = pane;
    await stream(pane, "clamp-reply", 10);
    const viewport = page.viewportSize()!;
    await send(body, "touchstart", true);
    // The finger rests at the very top, where a taller viewport clamps the scroll position.
    await body.evaluate((element) => {
      element.scrollTop = -element.scrollHeight;
    });
    await expect(page.getByText("Prompt 0", { exact: true })).toBeInViewport();
    const resting = await scrollHeight(body);
    await stream(pane, "clamp-reply", 10);
    expect(await scrollHeight(body)).toBe(resting);
    // A drag the pane doesn't follow (a nested scroller, an edge), then a clamp, no scrollend.
    await send(body, "touchmove", true);
    // Returning the promise wrapped in an object resolves this call once the listener is attached, so the
    // resize below can never scroll before it is listening.
    const clamped = await body.evaluateHandle((element) => ({
      scrolled: new Promise<number>((resolve) =>
        element.addEventListener("scroll", () => resolve(element.scrollTop), { once: true }),
      ),
    }));
    await page.setViewportSize({ width: viewport.width, height: viewport.height + 120 });
    await clamped.evaluate((armed) => armed.scrolled);
    await send(body, "touchend", false);
    await expect.poll(() => scrollHeight(body)).toBeGreaterThan(resting);
  });
});
