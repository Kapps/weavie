import { expect, type Page } from "@playwright/test";
import {
  type AgentPane,
  agentTurns,
  type OpenPanes,
  publishDelta,
  test,
  wheelUp,
} from "./harness/agent-pane";
import {
  deferredPhase,
  describeSample,
  frames,
  measureRender,
  type RenderSample,
} from "./harness/render-budget";
import { MOCK_AGENT_DEFAULTS } from "./mock-host";

// Render budgets for the agent pane, asserted as counts of layout-forcing reads, never as time. A scroll frame
// may read only the scroll position its listener is handed; output costs a constant per update. Run: pnpm e2e:perf.
const FRAMES = 60;
const UPDATES = 20;
// Keeping the reader still costs one scroll write per update, counted with the reads.
const READS_PER_UPDATE = 1;
const STYLE_LOOKUPS_PER_NOTCH = 6;

const history = agentTurns(60);

test.describe.configure({ timeout: 120_000 });
test.use({ trace: { mode: "retain-on-failure", snapshots: false } });

async function openPane(page: Page, openPanes: OpenPanes, name: string): Promise<AgentPane> {
  await deferredPhase.run(page);
  const pane = await openPanes({ [name]: history });
  pane.host.publishHost("settings", "agent-defaults", {
    ...MOCK_AGENT_DEFAULTS,
    progressDisplay: "latest",
  });
  await expect(page.getByText("Answer 59", { exact: true })).toBeVisible();
  return pane;
}

// Streams small deltas into a live reply, waiting for each to render.
async function stream(pane: AgentPane, count: number, itemId: string): Promise<void> {
  for (let step = 0; step < count; step++) {
    const index = publishDelta(pane, itemId);
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

// Streams a reply, then wheels up off the follow zone.
async function readAway(pane: AgentPane): Promise<void> {
  await stream(pane, 30, "live-reply");
  await wheelUp(pane, 400, 300);
}

test("scrolling while reading reads only the scroll position @perf", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(page, openPanes, "budget-scroll");
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
});

test("output while reading costs a constant per update @perf", async ({ page, openPanes }) => {
  const pane = await openPane(page, openPanes, "budget-reading");
  await readAway(pane);
  const scrolls = await countScrollEvents(pane);
  const sample = await measureRender(page, UPDATES, UPDATES * 8, () =>
    stream(pane, 1, "live-reply"),
  );
  expectOnlyPositionReads(sample, await scrolls(), UPDATES * READS_PER_UPDATE);
});

test("scrolling while output streams costs only the position and the updates @perf", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(page, openPanes, "budget-scroll-streaming");
  await readAway(pane);
  const scrolls = await countScrollEvents(pane);
  const sample = await measureRender(page, UPDATES, UPDATES * 8, async () => {
    // Toward the live reply, so it stays mounted while still below the follow zone.
    await scrollBy(pane, 5);
    await stream(pane, 1, "live-reply");
  });
  expectOnlyPositionReads(sample, await scrolls(), UPDATES * READS_PER_UPDATE);
});

test("output while following reads no layout @perf", async ({ page, openPanes }) => {
  const pane = await openPane(page, openPanes, "budget-following");
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
});

// The smoothed wheel writes the position once a frame, and each notch looks up whether an element under the pointer
// scrolls on its own: a style lookup per element on its path, measured 4.4 per notch, never a layout read.
test("wheeling while reading writes the position and reads no layout @perf", async ({
  page,
  openPanes,
}) => {
  const pane = await openPane(page, openPanes, "budget-wheel");
  await readAway(pane);
  const scrolls = await countScrollEvents(pane);
  const sample = await measureRender(page, FRAMES, FRAMES * 20, async () => {
    await page.mouse.wheel(0, -20);
    await frames(page, 1);
  });
  const report = describeSample(sample);
  const count = (api: string): number =>
    sample.sites.reduce((sum, [site, hits]) => (site.split(" ")[1] === api ? sum + hits : sum), 0);
  expect(count("scrollTop") + count("getComputedStyle"), report).toBe(sample.reads);
  expect(count("scrollTop"), report).toBeLessThanOrEqual(sample.frames + (await scrolls()));
  expect(count("getComputedStyle"), report).toBeLessThanOrEqual(FRAMES * STYLE_LOOKUPS_PER_NOTCH);
});
