import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, type Locator, type Page } from "@playwright/test";
import { test } from "./harness/network-fixtures";
import {
  deferredPhase,
  describeSample,
  frames,
  measureRender,
  type RenderSample,
} from "./harness/render-budget";
import { MockHost, type MockLiveSession, mockSession } from "./mock-host";

// Render budgets for the agent pane, asserted as counts of layout-forcing reads, never as time. A scroll frame
// may read only the scroll position its listener is handed; output costs a constant per update. Run: pnpm e2e:perf.
const FRAMES = 60;
const UPDATES = 20;
// Keeping the reader still costs one scroll write per update, counted with the reads.
const READS_PER_UPDATE = 1;
const STYLE_LOOKUPS_PER_NOTCH = 6;

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

const history = Array.from({ length: 60 }, (_, turn) => [
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
    text: `### Answer ${turn}\n\n${"A paragraph that wraps in the pane. ".repeat((turn % 5) + 2)}`,
  },
]).flat();

test.describe.configure({ timeout: 120_000 });
test.use({ trace: { mode: "retain-on-failure", snapshots: false } });

interface Pane {
  body: Locator;
  host: MockHost;
  page: Page;
  session: MockLiveSession;
  delta: number;
}

async function openPane(page: Page, name: string): Promise<Pane> {
  await deferredPhase.run(page);
  const session = mockSession(name, name, "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, { generation: 1, batchSize: 5000, messages: history });
  await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
  await host.waitUntilConnected();
  await expect(page.getByText("Answer 59", { exact: true })).toBeVisible();
  return { body: page.locator(".agent-body"), host, page, session, delta: 0 };
}

// Streams small deltas into a live reply, waiting for each to render.
async function stream(pane: Pane, count: number, itemId: string): Promise<void> {
  for (let step = 0; step < count; step++) {
    const index = pane.delta++;
    pane.host.publishAgentPane(pane.session.address, {
      providerId: "acp",
      type: "agent-message-delta",
      turnId: "live",
      itemId,
      itemType: "agentMessage",
      text: index % 6 === 5 ? `delta ${index}.\n\n` : `delta ${index} `,
    });
    await pane.page.waitForFunction(
      (text) => document.querySelector(".agent-body")?.textContent?.includes(text) === true,
      `delta ${index}`,
    );
    await frames(pane.page, 1);
  }
}

// Scrolls the way touch, momentum, and the scrollbar do: the engine moves, the pane only listens.
function scrollBy(pane: Pane, delta: number): Promise<void> {
  return pane.body.evaluate((element, by) => {
    element.scrollTop += by;
  }, delta);
}

// Counts scroll events on the pane, each of which the virtualizer's listener reads one position for.
async function countScrollEvents(pane: Pane): Promise<() => Promise<number>> {
  const counter = await pane.body.evaluateHandle((element) => {
    const state = { count: 0 };
    element.addEventListener("scroll", () => state.count++, { passive: true });
    return state;
  });
  return () => counter.evaluate((state) => state.count);
}

// Every read but the one scroll position each scroll event hands the virtualizer is the pane forcing layout.
function expectOnlyPositionReads(sample: RenderSample, scrolls: number, extra: number): void {
  const report = describeSample(sample);
  expect(sample.reads, report).toBeLessThanOrEqual(scrolls + extra);
  if (extra === 0) {
    for (const [site] of sample.sites) expect(site, report).toMatch(/^\S+ scrollTop {2}/);
  }
}

// Streams a reply, then wheels up off the follow zone and waits for the smoothing to settle.
async function readAway(pane: Pane): Promise<void> {
  await stream(pane, 30, "live-reply");
  await pane.body.hover();
  await pane.page.mouse.wheel(0, -400);
  await expect.poll(() => pane.body.evaluate((element) => -element.scrollTop)).toBeGreaterThan(300);
  await pane.body.evaluate(async (element) => {
    let previous = Number.NaN;
    for (let still = 0; still < 2; ) {
      await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
      still = element.scrollTop === previous ? still + 1 : 0;
      previous = element.scrollTop;
    }
  });
}

test("scrolling while reading reads only the scroll position @perf", async ({ page }) => {
  const pane = await openPane(page, "budget-scroll");
  try {
    await readAway(pane);
    const scrolls = await countScrollEvents(pane);
    const sample = await measureRender(page, FRAMES, FRAMES * 4, async () => {
      await scrollBy(pane, -30);
      await frames(page, 1);
    });
    const events = await scrolls();
    expectOnlyPositionReads(sample, events, 0);
    const report = describeSample(sample);
    expect(events, `every step must scroll\n${report}`).toBeGreaterThanOrEqual(FRAMES);
    expect(sample.reads, `the probe must still see Weavie's frames\n${report}`).toBeGreaterThan(0);
  } finally {
    await pane.host.close();
  }
});

test("output while reading costs a constant per update @perf", async ({ page }) => {
  const pane = await openPane(page, "budget-reading");
  try {
    await readAway(pane);
    const scrolls = await countScrollEvents(pane);
    const sample = await measureRender(page, UPDATES, UPDATES * 8, () =>
      stream(pane, 1, "live-reply"),
    );
    expectOnlyPositionReads(sample, await scrolls(), UPDATES * READS_PER_UPDATE);
  } finally {
    await pane.host.close();
  }
});

test("scrolling while output streams costs only the position and the updates @perf", async ({
  page,
}) => {
  const pane = await openPane(page, "budget-scroll-streaming");
  try {
    await readAway(pane);
    const scrolls = await countScrollEvents(pane);
    const sample = await measureRender(page, UPDATES, UPDATES * 8, async () => {
      // Toward the live reply, so it stays mounted while still below the follow zone.
      await scrollBy(pane, 5);
      await stream(pane, 1, "live-reply");
    });
    expectOnlyPositionReads(sample, await scrolls(), UPDATES * READS_PER_UPDATE);
  } finally {
    await pane.host.close();
  }
});

test("output while following reads no layout @perf", async ({ page }) => {
  const pane = await openPane(page, "budget-following");
  try {
    // Short replies, so none outgrows the viewport and pins at its top.
    await stream(pane, 4, "reply-start");
    expect(await pane.body.evaluate((element) => element.scrollTop)).toBe(0);
    const scrolls = await countScrollEvents(pane);
    let reply = 0;
    const sample = await measureRender(page, UPDATES, UPDATES * 8, () =>
      stream(pane, 1, `reply-${Math.floor(reply++ / 4)}`),
    );
    expectOnlyPositionReads(sample, await scrolls(), 0);
    expect(await pane.body.evaluate((element) => element.scrollTop)).toBe(0);
  } finally {
    await pane.host.close();
  }
});

// The smoothed wheel writes the position once a frame, and each notch looks up whether an element under the pointer
// scrolls on its own: a style lookup per element on its path, measured 4.4 per notch, never a layout read.
test("wheeling while reading writes the position and reads no layout @perf", async ({ page }) => {
  const pane = await openPane(page, "budget-wheel");
  try {
    await readAway(pane);
    const scrolls = await countScrollEvents(pane);
    const sample = await measureRender(page, FRAMES, FRAMES * 20, async () => {
      await page.mouse.wheel(0, -20);
      await frames(page, 1);
    });
    const report = describeSample(sample);
    const count = (api: string): number =>
      sample.sites.reduce(
        (sum, [site, hits]) => (site.split(" ")[1] === api ? sum + hits : sum),
        0,
      );
    expect(count("scrollTop") + count("getComputedStyle"), report).toBe(sample.reads);
    expect(count("scrollTop"), report).toBeLessThanOrEqual(sample.frames + (await scrolls()));
    expect(count("getComputedStyle"), report).toBeLessThanOrEqual(FRAMES * STYLE_LOOKUPS_PER_NOTCH);
  } finally {
    await pane.host.close();
  }
});
