import { existsSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { expect, test } from "@playwright/test";
import { MockHost, mockEditorOptions, mockSession } from "./mock-host";

// Scrolling back through history the pane has never measured used to stutter: the virtualizer answers each
// first measurement with a scroll correction, and resolving that correction against its own cached offset
// wrote back a position the live scroll had already moved past — one frame of motion discarded per newly
// measured row, smooth again only on a second pass over the same range. This drives the pane up at an exact
// constant rate and asserts the on-screen content tracks it while those corrections are still landing.

const distDir = join(dirname(fileURLToPath(import.meta.url)), "..", "dist");

test.beforeAll(() => {
  if (!existsSync(join(distDir, "index.html"))) {
    throw new Error(
      `built app not found at ${distDir}; run \`pnpm run build\` before the e2e tests`,
    );
  }
});

// Turns of prompt → collapsed tool activity → answer, so the range holds every entry shape the estimator
// guesses at, each guess wrong by a different amount.
const transcript = Array.from({ length: 150 }, (_, turn) => [
  {
    providerId: "acp",
    type: "user-message",
    turnId: `t${turn}`,
    itemId: `u-${turn}`,
    text: `Prompt ${turn}: please look into the thing and report back.`,
  },
  ...Array.from({ length: 4 }, (_, step) => ({
    providerId: "acp",
    type: "item-completed",
    turnId: `t${turn}`,
    itemId: `tool-${turn}-${step}`,
    itemType: "commandExecution",
    status: "completed",
    title: `grep -rn "thing" src/step${step}`,
    text: `src/file${step}.ts:${step * 7 + 3}: matched the thing`,
  })),
  {
    providerId: "acp",
    type: "item-completed",
    turnId: `t${turn}`,
    itemId: `a-${turn}`,
    itemType: "agentMessage",
    status: "completed",
    text: `### Answer ${turn}\n\n${Array.from(
      { length: (turn % 4) + 1 },
      (_, paragraph) =>
        `Paragraph ${paragraph + 1} of turn ${turn} with **formatted** content that wraps in the pane.`,
    ).join("\n\n")}`,
  },
]).flat();

test("scrolling back through never-measured history stays smooth", async ({ page }) => {
  const session = mockSession("scroll", "scroll", "acp");
  const host = await MockHost.start({ distDir, sessions: [session] });
  host.setAgentHistory(session.address, { generation: 1, pageSize: 5000, messages: transcript });

  try {
    await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
    await host.waitUntilConnected();
    const body = page.locator(".agent-body");
    await expect(body).toBeVisible();
    await expect(page.getByText("Answer 149", { exact: true })).toBeVisible({ timeout: 60_000 });
    await body.evaluate((element) => {
      element.scrollTo({ top: element.scrollHeight });
    });
    await page.waitForTimeout(1000);

    // Scroll up one exact step per frame. Every tracked row must move by exactly that step: the pane owes
    // the reader the motion it was given, whatever it is re-measuring underneath.
    const measured = await body.evaluate(async (element: HTMLElement) => {
      const rate = 8;
      const descriptor = Object.getOwnPropertyDescriptor(Element.prototype, "scrollTop");
      if (descriptor?.get === undefined || descriptor.set === undefined) {
        throw new Error("scrollTop is not an accessor");
      }
      const read = descriptor.get;
      const write = descriptor.set;
      let corrections = 0;
      let stepping = false;
      // Count every way the pane can move the scroll position, so the guard below stays about whether
      // corrections happened at all rather than which API applies them.
      Object.defineProperty(element, "scrollTop", {
        configurable: true,
        get(): number {
          return read.call(this) as number;
        },
        set(value: number) {
          if (!stepping) {
            corrections += 1;
          }
          write.call(this, value);
        },
      });
      const scrollTo = element.scrollTo.bind(element);
      element.scrollTo = ((options: ScrollToOptions) => {
        corrections += 1;
        scrollTo(options);
      }) as typeof element.scrollTo;
      const deviations: number[] = [];
      let previous: { id: string; top: number } | null = null;
      for (let frame = 0; frame < 500; frame++) {
        await new Promise<void>((resolve) => requestAnimationFrame(() => resolve()));
        const viewportTop = element.getBoundingClientRect().top;
        let highest: { id: string; top: number } | null = null;
        for (const row of document.querySelectorAll<HTMLElement>(".agent-virtual-row")) {
          const id = row.dataset.transcriptEntry;
          const top = row.getBoundingClientRect().top - viewportTop;
          if (id !== undefined && top >= 0 && (highest === null || top < highest.top)) {
            highest = { id, top };
          }
        }
        if (highest !== null && previous?.id === highest.id) {
          deviations.push(highest.top - previous.top - rate);
        }
        previous = highest;
        stepping = true;
        element.scrollTop -= rate;
        stepping = false;
      }
      return {
        corrections,
        stutters: deviations.filter((deviation) => Math.abs(deviation) > 2).length,
      };
    });

    // The corrections have to be real, or a pane that simply stopped compensating would pass.
    expect(measured.corrections).toBeGreaterThan(10);
    expect(measured.stutters).toBe(0);
  } finally {
    await host.close();
  }
});

test.use({
  launchOptions: {
    executablePath: process.env.WEAVIE_CHROMIUM,
    args: ["--enable-smooth-scrolling"],
  },
});

test.describe("native wheel scrolling", () => {
  test.use({ viewport: { width: 1000, height: 800 } });

  for (const smoothScrolling of [true, false]) {
    test(`wheel notches preserve their distance without overlapping history rows (smooth=${smoothScrolling})`, async ({
      page,
    }) => {
      const session = mockSession("wheel-scroll", "wheel-scroll", "acp");
      const host = await MockHost.start({ distDir, sessions: [session] });
      host.setAgentHistory(session.address, {
        generation: 1,
        pageSize: 5000,
        messages: transcript.map((message) =>
          message.type === "user-message" ? { ...message, text: message.text.repeat(4) } : message,
        ),
      });
      try {
        await page.goto(host.pageUrl(), { waitUntil: "domcontentloaded" });
        await host.waitUntilConnected();
        host.publishHost("settings", "editorOptions", mockEditorOptions({ smoothScrolling }));
        const body = page.locator(".agent-body");
        await expect(page.getByText("Answer 149", { exact: true })).toBeVisible();
        await body.evaluate((element) => element.scrollTo({ top: element.scrollHeight }));
        await page.waitForTimeout(500);
        await body.hover();
        const overlaps: number[] = [];
        const distances: number[] = [];
        const movingFrames: number[] = [];
        for (let notch = 0; notch < 32; notch++) {
          const anchor = await body.evaluate(readVisibleAnchor);
          const sampling = body.evaluate(sampleWheelFrames);
          await page.mouse.wheel(0, -120);
          const sample = await sampling;
          overlaps.push(sample.overlap);
          movingFrames.push(sample.frames);
          distances.push(await body.evaluate(anchorDistance, anchor));
        }
        const rapidAnchor = await body.evaluate(readVisibleAnchor);
        const rapidSampling = body.evaluate(sampleWheelFrames);
        for (let notch = 0; notch < 3; notch++) await page.mouse.wheel(0, -120);
        overlaps.push((await rapidSampling).overlap);
        const rapidDistance = await body.evaluate(anchorDistance, rapidAnchor);
        expect(
          Math.abs(rapidDistance - 360),
          "rapid notches must retain accumulated distance",
        ).toBeLessThanOrEqual(1);
        const burstSampling = body.evaluate(sampleWheelFrames);
        await page.mouse.wheel(0, -2000);
        overlaps.push((await burstSampling).overlap);
        expect(
          Math.max(...overlaps),
          "visible transcript rows must never paint over each other",
        ).toBeLessThanOrEqual(1);
        expect(
          Math.max(...distances.map((distance) => Math.abs(distance - 120))),
          "every notch must move visible content the requested 120 pixels",
        ).toBeLessThanOrEqual(1);
        if (smoothScrolling)
          expect(Math.max(...movingFrames), "smooth scrolling must animate").toBeGreaterThan(3);
        const latest = page.getByRole("button", { name: "Jump to latest", exact: true });
        const bounds = await body.boundingBox();
        if (bounds === null) throw new Error("Agent scroll pane has no bounds");
        const clientWidth = await body.evaluate((element) => element.clientWidth);
        await page.mouse.move(bounds.x + clientWidth - 20, bounds.y + bounds.height / 2);
        await expect(page.locator(".agent-scroll-nav")).toHaveCSS("opacity", "1");
        // Keep the pending wheel and navigation in one task so the animation cannot finish
        // while Playwright waits for the button to become stable enough to click.
        await body.evaluate((element) => {
          element.dispatchEvent(
            new WheelEvent("wheel", { bubbles: true, cancelable: true, deltaY: -120 }),
          );
          const button = document.querySelector<HTMLButtonElement>(".agent-scroll-nav-latest");
          if (button === null) throw new Error("Jump to latest is unavailable");
          button.click();
        });
        await expect(latest).toBeHidden();
        await page.waitForTimeout(200);
        expect(
          await body.evaluate(
            (element) => element.scrollHeight - element.clientHeight - element.scrollTop,
          ),
        ).toBeLessThanOrEqual(1);
      } finally {
        await host.close();
      }
    });
  }
});

async function sampleWheelFrames(
  element: HTMLElement,
): Promise<{ overlap: number; frames: number }> {
  let overlap = 0;
  const offsets = new Set<number>();
  const start = performance.now();
  while (performance.now() - start < 400) {
    // ResizeObserver runs after animation callbacks but before paint; sample after rendering so
    // intermediate layout that never reaches the screen is not counted as a visible overlap.
    await new Promise<void>((resolve) => requestAnimationFrame(() => setTimeout(resolve, 0)));
    offsets.add(element.scrollTop);
    const viewport = element.getBoundingClientRect();
    const rows = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row"))
      .map((row) => row.getBoundingClientRect())
      .filter((row) => row.bottom > viewport.top && row.top < viewport.bottom)
      .sort((a, b) => a.top - b.top);
    for (let index = 1; index < rows.length; index++) {
      overlap = Math.max(overlap, rows[index - 1]!.bottom - rows[index]!.top);
    }
  }
  return { overlap, frames: offsets.size };
}

function readVisibleAnchor(element: HTMLElement): { id: string; top: number } {
  const viewport = element.getBoundingClientRect();
  const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
    (candidate) => {
      const bounds = candidate.getBoundingClientRect();
      return bounds.bottom > viewport.top && bounds.top < viewport.bottom;
    },
  );
  if (row === undefined) throw new Error("No visible transcript anchor");
  return { id: row.dataset.transcriptEntry!, top: row.getBoundingClientRect().top };
}

function anchorDistance(element: HTMLElement, previous: { id: string; top: number }): number {
  const row = Array.from(element.querySelectorAll<HTMLElement>(".agent-virtual-row")).find(
    (candidate) => candidate.dataset.transcriptEntry === previous.id,
  );
  if (row === undefined) throw new Error("Wheel notches lost the visible transcript anchor");
  return row.getBoundingClientRect().top - previous.top;
}
